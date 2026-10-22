using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Tencent Cloud real-time speech recognition over WebSocket. One instance is
/// one connection/voice_id; a source change or reconnect must create a new one.
/// Audio is queued from the capture thread and sent at roughly 1:1 real time by
/// a background writer, so a slow network never blocks the audio callback.
/// </summary>
public sealed class TencentRealtimeAsrClient : ITencentRealtimeRecognizer
{
    private const string Host = "asr.cloud.tencent.com";
    private const int SampleRate = 16_000;
    private const int BytesPerChunk = 6_400; // 200 ms of 16 kHz/16-bit mono PCM
    private const int RingSeconds = 60;

    private readonly TencentAsrSettings _settings;
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<SendItem> _queue = Channel.CreateBounded<SendItem>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _audioSync = new();
    private readonly float[] _ring = new float[SampleRate * RingSeconds];
    private readonly List<byte> _pending = [];
    private Task? _sendTask;
    private Task? _receiveTask;
    private long _ringTotal;
    private int _started;
    private int _faulted;
    private int _finalReceived;
    private int _lastFinalIndex = -1;
    private int _lastPartialIndex = -1;
    private string _lastPartialText = string.Empty;

    public TencentRealtimeAsrClient(TencentAsrSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public event EventHandler<TencentRealtimeResult>? ResultReady;

    public event EventHandler<TencentRealtimePartial>? PartialReady;

    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            if (!_settings.IsConfigured)
                throw new TencentAsrException("NotConfigured", "腾讯云 ASR 尚未配置 AppID/SecretId/SecretKey。");
            if (!TencentAsrEngines.IsRealtimeEngine(_settings.Engine))
                throw new TencentAsrException("InvalidEngine", "不支持的腾讯云实时识别语言。");

            var uri = BuildUri();
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

            _receiveTask = ReceiveLoopAsync(_cts.Token);
            _sendTask = SendLoopAsync(_cts.Token);
            await _handshake.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
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
            Fault(new InvalidOperationException("腾讯云实时识别只接受 16 kHz 音频。"));
            return;
        }

        var pcm = ToPcm16(samples.Span);
        var overflow = false;
        lock (_audioSync)
        {
            AppendRing(samples.Span);
            _pending.AddRange(pcm);
            while (_pending.Count >= BytesPerChunk)
            {
                var chunk = _pending.GetRange(0, BytesPerChunk).ToArray();
                _pending.RemoveRange(0, BytesPerChunk);
                if (!_queue.Writer.TryWrite(new SendItem(chunk, IsEnd: false)))
                {
                    overflow = true;
                    break;
                }
            }
        }

        if (overflow) Fault(new InvalidOperationException("实时识别发送队列已满，连接跟不上音频。"));
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0) return;

        var overflow = false;
        lock (_audioSync)
        {
            if (_pending.Count > 0)
            {
                var tail = _pending.ToArray();
                _pending.Clear();
                if (!_queue.Writer.TryWrite(new SendItem(tail, IsEnd: false))) overflow = true;
            }
        }

        if (overflow) Fault(new InvalidOperationException("实时识别发送队列已满，无法发送结束帧。"));
        _queue.Writer.TryWrite(new SendItem([], IsEnd: true));

        try
        {
            await _completed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The server did not confirm the final frame. The caller still gets
            // every stable sentence already raised; close the socket below.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Authentication/resource errors are already reported through Faulted.
        }

        await CloseAsync().ConfigureAwait(false);
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildUri()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = timestamp + 3600;
        var nonce = Random.Shared.Next(1, int.MaxValue);
        var voiceId = Guid.NewGuid().ToString("N");
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine_model_type"] = _settings.Engine,
            ["expired"] = expired.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["needvad"] = "1",
            ["nonce"] = nonce.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["secretid"] = _settings.SecretId,
            ["timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["voice_format"] = "1",
            ["voice_id"] = voiceId,
        };
        if (SupportsWordTimestamps(_settings.Engine))
        {
            // Needed to split one stable sentence at a local speaker change.
            parameters["word_info"] = "1";
        }

        var canonical = string.Join("&", parameters.Select(pair => $"{pair.Key}={pair.Value}"));
        var signText = $"{Host}/asr/v2/{_settings.AppId}?{canonical}";
        var signature = Convert.ToBase64String(
            HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(_settings.SecretKey),
                Encoding.UTF8.GetBytes(signText)));
        var query = string.Join("&", parameters.Select(pair =>
            $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(
            $"wss://{Host}/asr/v2/{_settings.AppId}?{query}&signature={Uri.EscapeDataString(signature)}");
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    if (item.IsEnd)
                    {
                        var end = Encoding.UTF8.GetBytes("{\"type\":\"end\"}");
                        await _socket.SendAsync(end, WebSocketMessageType.Text, true, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await _socket.SendAsync(item.Data, WebSocketMessageType.Binary, true, cancellationToken)
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
        var buffer = new byte[8 * 1024];
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
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (Volatile.Read(ref _finalReceived) == 1)
        {
            // The server closes right after the final frame without a close
            // handshake. That is a normal end of stream, not a failure.
        }
        catch (Exception exception)
        {
            Fault(exception);
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    private void HandleMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
        if (code != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "腾讯云实时识别失败。"
                : "腾讯云实时识别失败。";
            var exception = new TencentAsrException($"Realtime{code}", message);
            _handshake.TrySetException(exception);
            Fault(exception);
            return;
        }

        _handshake.TrySetResult();
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("slice_type", out var sliceElement))
        {
            if (root.TryGetProperty("final", out var onlyFinal) && onlyFinal.GetInt32() == 1)
            {
                Interlocked.Exchange(ref _finalReceived, 1);
                _completed.TrySetResult();
            }

            return;
        }

        var text = result.TryGetProperty("voice_text_str", out var textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
        var index = result.TryGetProperty("index", out var indexElement)
            ? indexElement.GetInt32()
            : -1;
        var messageId = root.TryGetProperty("message_id", out var idElement)
            ? idElement.GetString()
            : null;
        var sliceType = sliceElement.GetInt32();

        if (sliceType == 1 && !string.IsNullOrWhiteSpace(text))
        {
            if (index < 0 ||
                index > _lastPartialIndex ||
                (index == _lastPartialIndex && !string.Equals(text, _lastPartialText, StringComparison.Ordinal)))
            {
                _lastPartialIndex = index < 0 ? _lastPartialIndex : index;
                _lastPartialText = text;
                try
                {
                    PartialReady?.Invoke(this, new TencentRealtimePartial(text, index, messageId));
                }
                catch
                {
                    // A presentation subscriber must never break the socket loop.
                }
            }
        }
        else if (sliceType == 2)
        {
            if (index >= 0 && index <= Volatile.Read(ref _lastFinalIndex)) return;
            if (index >= 0) Volatile.Write(ref _lastFinalIndex, index);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var startMs = result.TryGetProperty("start_time", out var startElement)
                    ? startElement.GetInt32()
                    : 0;
                var endMs = result.TryGetProperty("end_time", out var endElement)
                    ? endElement.GetInt32()
                    : startMs;
                var audio = ExtractAudio(startMs, endMs);
                var words = ReadWords(result);
                try
                {
                    ResultReady?.Invoke(
                        this,
                        new TencentRealtimeResult(
                            text,
                            messageId,
                            TimeSpan.FromMilliseconds(startMs),
                            TimeSpan.FromMilliseconds(endMs),
                            audio,
                            words));
                }
                catch
                {
                    // A presentation subscriber must never break the socket loop.
                }
            }
        }

        if (root.TryGetProperty("final", out var finalElement) && finalElement.GetInt32() == 1)
        {
            Interlocked.Exchange(ref _finalReceived, 1);
            _completed.TrySetResult();
        }
    }

    private void Fault(Exception exception)
    {
        if (Volatile.Read(ref _finalReceived) == 1) return;
        if (Interlocked.Exchange(ref _faulted, 1) == 1) return;
        _handshake.TrySetException(exception);
        _completed.TrySetException(exception);
        try { Faulted?.Invoke(this, exception); }
        catch { /* diagnostics must not break the socket loop */ }
    }

    private static bool SupportsWordTimestamps(string? engine) => engine is
        TencentAsrEngines.Chinese or TencentAsrEngines.English or
        TencentAsrEngines.Japanese or TencentAsrEngines.RealtimeChineseEnglish;

    private static IReadOnlyList<TencentRealtimeWord> ReadWords(JsonElement result)
    {
        if (!result.TryGetProperty("word_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var words = new List<TencentRealtimeWord>();
        foreach (var item in list.EnumerateArray())
        {
            var text = item.TryGetProperty("word", out var word) ? word.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var start = item.TryGetProperty("start_time", out var startElement) ? startElement.GetInt32() : 0;
            var end = item.TryGetProperty("end_time", out var endElement) ? endElement.GetInt32() : start;
            words.Add(new TencentRealtimeWord(
                text,
                TimeSpan.FromMilliseconds(start),
                TimeSpan.FromMilliseconds(end)));
        }

        return words;
    }

    private void AppendRing(ReadOnlySpan<float> samples)
    {
        var write = (int)(_ringTotal % _ring.Length);
        var first = Math.Min(samples.Length, _ring.Length - write);
        samples[..first].CopyTo(_ring.AsSpan(write));
        if (first < samples.Length)
        {
            samples[first..].CopyTo(_ring);
        }

        _ringTotal += samples.Length;
    }

    private float[] ExtractAudio(int startMs, int endMs)
    {
        lock (_audioSync)
        {
            if (endMs <= startMs) return [];
            var oldest = Math.Max(0, _ringTotal - _ring.Length);
            var start = Math.Clamp((long)startMs * SampleRate / 1000, oldest, _ringTotal);
            var end = Math.Clamp((long)endMs * SampleRate / 1000, start, _ringTotal);
            var length = (int)(end - start);
            if (length <= 0) return [];

            var result = new float[length];
            var read = (int)(start % _ring.Length);
            var first = Math.Min(length, _ring.Length - read);
            Array.Copy(_ring, read, result, 0, first);
            if (first < length)
            {
                Array.Copy(_ring, 0, result, first, length - first);
            }

            return result;
        }
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
            // The server normally closes first after final; nothing to recover.
        }
        finally
        {
            _cts.Cancel();
        }
    }

    private static byte[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = Math.Clamp(samples[i], -1f, 1f);
            var pcm = (short)Math.Round(value * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), pcm);
        }

        return bytes;
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
        catch { /* disposal is best-effort */ }
    }

    private readonly record struct SendItem(byte[] Data, bool IsEnd);
}

/// <summary>Creates one real-time connection for the capture session.</summary>
public sealed class TencentRealtimeAsrClientFactory : ITencentRealtimeRecognizerFactory
{
    public ITencentRealtimeRecognizer Create(TencentAsrSettings settings) => new TencentRealtimeAsrClient(settings);
}
