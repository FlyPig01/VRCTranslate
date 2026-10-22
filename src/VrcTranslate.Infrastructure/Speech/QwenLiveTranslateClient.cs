using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Alibaba Cloud Bailian Qwen LiveTranslate over WebSocket. It streams 16 kHz
/// PCM, receives the source transcript, translated text and speaker id from the
/// same response, and never calls the local speaker models.
/// </summary>
public sealed class QwenLiveTranslateClient : IQwenLiveTranslateClient
{
    private const int SampleRate = 16_000;
    private const int ChunkBytes = 3_200; // 100 ms

    private readonly QwenAsrSettings _settings;
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<string> _sendQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, int> _speakerBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceByTranslation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originalBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _translationByItem = new(StringComparer.Ordinal);
    private readonly List<byte> _pending = [];
    private Task? _sendTask;
    private Task? _receiveTask;
    private string? _lastSourceItem;
    private int _started;
    private int _faulted;

    public QwenLiveTranslateClient(QwenAsrSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public event EventHandler<QwenCaptionUpdate>? CaptionUpdated;

    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            if (!_settings.IsConfigured)
                throw new InvalidOperationException("阿里云实时翻译尚未配置 API Key / Workspace ID。");

            var uri = BuildUri();
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey);
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _receiveTask = ReceiveLoopAsync(_cts.Token);

            var update = JsonSerializer.Serialize(new
            {
                type = "session.update",
                session = new
                {
                    output_modalities = new[] { "text" },
                    audio = new
                    {
                        input = new
                        {
                            turn_detection = new { type = "speaker_detection", threshold = 0.5 },
                        },
                    },
                    translation = new { language = _settings.TargetLanguage },
                },
            });
            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(update), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            _sendTask = SendLoopAsync(_cts.Token);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _cts.Cancel();
            throw;
        }
    }

    public void Append(ReadOnlyMemory<float> samples, int sampleRate)
    {
        if (Volatile.Read(ref _started) == 0 || samples.IsEmpty) return;
        if (sampleRate != SampleRate)
        {
            Fault(new InvalidOperationException("Qwen 实时翻译只接受 16 kHz 音频。"));
            return;
        }

        var pcm = ToPcm16(samples.Span);
        lock (_pending)
        {
            _pending.AddRange(pcm);
            while (_pending.Count >= ChunkBytes)
            {
                var chunk = _pending.GetRange(0, ChunkBytes).ToArray();
                _pending.RemoveRange(0, ChunkBytes);
                Enqueue(chunk);
            }
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0) return;

        lock (_pending)
        {
            if (_pending.Count > 0)
            {
                Enqueue(_pending.ToArray());
                _pending.Clear();
            }
        }

        _sendQueue.Writer.TryWrite("{\"type\":\"session.finish\"}");
        try
        {
            await _finished.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The server may close after the final response; keep collected captions.
        }

        await CloseAsync().ConfigureAwait(false);
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Enqueue(byte[] pcm)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm),
        });
        if (!_sendQueue.Writer.TryWrite(json))
            Fault(new InvalidOperationException("Qwen 发送队列已满，连接跟不上音频。"));
    }

    private Uri BuildUri()
    {
        var host = _settings.Region switch
        {
            "ap-southeast-1" => $"{_settings.WorkspaceId}.ap-southeast-1.maas.aliyuncs.com",
            _ => $"{_settings.WorkspaceId}.cn-beijing.maas.aliyuncs.com",
        };
        return new Uri(
            $"wss://{host}/api-ws/v1/realtime?model={Uri.EscapeDataString(_settings.Model)}");
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _sendQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_sendQueue.Reader.TryRead(out var message))
                {
                    await _socket.SendAsync(
                        Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fault(exception);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                HandleEvent(json);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fault(exception);
        }
        finally
        {
            _finished.TrySetResult();
        }
    }

    private void HandleEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        switch (type)
        {
            case "session.created":
                break;
            case "session.updated":
                _ready.TrySetResult();
                break;
            case "session.finished":
                _finished.TrySetResult();
                break;
            case "error":
                Fault(new InvalidOperationException(
                    root.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message)
                        ? message.GetString() ?? "Qwen 实时翻译失败。"
                        : "Qwen 实时翻译失败。"));
                break;
            case "input_audio_buffer.speech_started":
                HandleSpeechStarted(root);
                break;
            case "conversation.item.created":
                HandleItemCreated(root);
                break;
            case "conversation.item.input_audio_transcription.delta":
                HandleSourceText(root, final: false, appendDelta: true);
                break;
            case "conversation.item.input_audio_transcription.text":
                HandleSourceText(root, final: false, appendDelta: false);
                break;
            case "conversation.item.input_audio_transcription.completed":
                HandleSourceText(root, final: true, appendDelta: false);
                break;
            case "response.text.delta":
                HandleTranslationDelta(root);
                break;
            case "response.text.done":
                HandleTranslationDone(root);
                break;
            case "response.done":
                HandleResponseDone(root);
                break;
        }
    }

    private void HandleSpeechStarted(JsonElement root)
    {
        if (!root.TryGetProperty("item_id", out var item) || item.GetString() is not { } itemId) return;
        _lastSourceItem = itemId;
        if (root.TryGetProperty("speaker_id", out var speaker) && speaker.TryGetInt32(out var id))
            _speakerBySource[itemId] = id;
    }

    private void HandleItemCreated(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.GetString() is not { } itemId) return;
        if (root.TryGetProperty("previous_item_id", out var previous) &&
            previous.GetString() is { Length: > 0 } source)
        {
            _sourceByTranslation[itemId] = source;
        }
    }

    private void HandleSourceText(JsonElement root, bool final, bool appendDelta)
    {
        if (!root.TryGetProperty("item_id", out var item) || item.GetString() is not { } itemId) return;
        _lastSourceItem = itemId;
        if (final)
        {
            var transcript = ReadString(root, "transcript");
            if (string.IsNullOrWhiteSpace(transcript)) return;
            _originalBySource[itemId] = transcript.Trim();
        }
        else if (appendDelta)
        {
            // The delta form sends only the new characters; overwriting here is
            // what made the live block look like it contained just a few words.
            var delta = ReadString(root, "delta");
            if (string.IsNullOrEmpty(delta)) return;
            _originalBySource[itemId] =
                (_originalBySource.GetValueOrDefault(itemId) ?? string.Empty) + delta;
        }
        else
        {
            // The text form carries the confirmed text plus the provisional stash.
            var text = ReadString(root, "text") + ReadString(root, "stash");
            if (string.IsNullOrWhiteSpace(text)) return;
            _originalBySource[itemId] = text.Trim();
        }

        EmitForSource(itemId, final: false);
    }

    private void HandleTranslationDelta(JsonElement root)
    {
        if (!root.TryGetProperty("item_id", out var item) || item.GetString() is not { } itemId) return;
        MapFallbackSource(itemId);
        var delta = ReadString(root, "delta");
        if (delta.Length == 0) return;
        _translationByItem[itemId] = (_translationByItem.GetValueOrDefault(itemId) ?? string.Empty) + delta;
        Emit(itemId, final: false);
    }

    private void HandleTranslationDone(JsonElement root)
    {
        if (!root.TryGetProperty("item_id", out var item) || item.GetString() is not { } itemId) return;
        MapFallbackSource(itemId);
        var text = ReadString(root, "text");
        if (text.Length > 0) _translationByItem[itemId] = text;
        Emit(itemId, final: false);
    }

    private void HandleResponseDone(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array) return;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.GetString() is not { } itemId) continue;
            var text = string.Empty;
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    text += ReadString(part, "text") + ReadString(part, "transcript");
                }
            }

            MapFallbackSource(itemId);
            if (text.Length > 0) _translationByItem[itemId] = text;
            Emit(itemId, final: true);
        }

        _finished.TrySetResult();
    }

    private void MapFallbackSource(string translationItem)
    {
        if (!_sourceByTranslation.ContainsKey(translationItem) && _lastSourceItem is not null)
            _sourceByTranslation[translationItem] = _lastSourceItem;
    }

    private void EmitForSource(string sourceItem, bool final)
    {
        foreach (var pair in _sourceByTranslation)
        {
            if (string.Equals(pair.Value, sourceItem, StringComparison.Ordinal))
                Emit(pair.Key, final);
        }
    }

    private void Emit(string translationItem, bool final)
    {
        var source = _sourceByTranslation.GetValueOrDefault(translationItem) ?? _lastSourceItem ?? translationItem;
        var original = _originalBySource.GetValueOrDefault(source) ?? string.Empty;
        var translation = _translationByItem.GetValueOrDefault(translationItem) ?? string.Empty;
        var speaker = _speakerBySource.TryGetValue(source, out var id) ? id : (int?)null;
        try
        {
            CaptionUpdated?.Invoke(
                this,
                new QwenCaptionUpdate(translationItem, original, translation, speaker, null, final));
        }
        catch
        {
            // Presentation subscribers must not break the socket loop.
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static byte[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var pcm = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), pcm);
        }

        return bytes;
    }

    private void Fault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 1) return;
        _ready.TrySetException(exception);
        _finished.TrySetException(exception);
        try { Faulted?.Invoke(this, exception); }
        catch { }
    }

    private async Task CloseAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            _cts.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await CloseAsync().ConfigureAwait(false); } catch { }
        if (_sendTask is not null) await IgnoreAsync(_sendTask).ConfigureAwait(false);
        if (_receiveTask is not null) await IgnoreAsync(_receiveTask).ConfigureAwait(false);
        _socket.Dispose();
        _cts.Dispose();
    }

    private static async Task IgnoreAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }
}
