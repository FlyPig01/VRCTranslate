using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Speech;

/// <summary>
/// Wires one loopback capture to Qwen LiveTranslate. Qwen owns sentence
/// segmentation, source transcription, translation and speaker detection, so
/// this session never runs local VAD or voiceprint models.
/// </summary>
public sealed class QwenSpeechCaptureSession : ISpeechCaptureSession
{
    private const int SampleRate = 16_000;

    private readonly IAudioCapture _capture;
    private readonly IQwenLiveTranslateClient _client;
    private readonly Dictionary<int, string> _speakerLabels = [];
    private readonly object _sync = new();
    private readonly CancellationTokenSource _sourceCts = new();
    private AudioSourceState _sourceState;
    private bool _started;
    private bool _disposed;

    public QwenSpeechCaptureSession(IAudioCapture capture, IQwenLiveTranslateClient client)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sourceState = new AudioSourceState(capture.SourceKind);
        _capture.SamplesReady += OnSamplesReady;
        _capture.SourceChanged += OnSourceChanged;
        _capture.Faulted += OnCaptureFaulted;
        _client.CaptionUpdated += OnCaptionUpdated;
        _client.Faulted += OnClientFaulted;
    }

    public event EventHandler<SpeechRecognitionResult>? ResultReady
    {
        add { }
        remove { }
    }

    public event EventHandler<TencentRealtimePartial>? PartialReady
    {
        add { }
        remove { }
    }

    public event EventHandler<QwenCaptionUpdate>? CaptionUpdated;

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public event EventHandler<string>? Diagnostic;

    public event EventHandler<SpeechSegmenterFallbackEventArgs>? SegmenterFallback
    {
        add { }
        remove { }
    }

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public AudioSourceState SourceState
    {
        get { lock (_sync) return _sourceState; }
    }

    public bool ConsumeFallbackNotice() => false;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(QwenSpeechCaptureSession));
            if (_started) return;
            _started = true;
        }

        try
        {
            // Establish the cloud session first; starting capture earlier would
            // push audio before session.update is acknowledged and drop it.
            await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) _started = false;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
        }

        try { await _capture.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            try { await _client.CompleteAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            if (IsStarted) await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _capture.SamplesReady -= OnSamplesReady;
            _capture.SourceChanged -= OnSourceChanged;
            _capture.Faulted -= OnCaptureFaulted;
            _client.CaptionUpdated -= OnCaptionUpdated;
            _client.Faulted -= OnClientFaulted;
            await _capture.DisposeAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            _sourceCts.Cancel();
            _sourceCts.Dispose();
        }
    }

    private void OnSamplesReady(object? sender, AudioSamplesEventArgs args)
    {
        if (!IsStarted) return;
        var (rms, peak) = Measure(args.Samples.Span);
        try { LevelChanged?.Invoke(this, new AudioLevelEventArgs(rms, peak)); }
        catch (Exception exception) { RaiseFaulted(exception); }
        if (args.SampleRate != SampleRate)
        {
            RaiseFaulted(new InvalidOperationException("音频采样率必须为 16 kHz。"));
            return;
        }

        try { _client.Append(args.Samples, args.SampleRate); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private void OnCaptionUpdated(object? sender, QwenCaptionUpdate update)
    {
        var label = update.SpeakerId is { } id ? LabelFor(id) : update.SpeakerLabel;
        var localized = update with { SpeakerLabel = label };
        try { CaptionUpdated?.Invoke(this, localized); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private string LabelFor(int speakerId)
    {
        lock (_speakerLabels)
        {
            if (_speakerLabels.TryGetValue(speakerId, out var label)) return label;
            label = ((char)('A' + _speakerLabels.Count % 26)).ToString();
            _speakerLabels[speakerId] = label;
            return label;
        }
    }

    private void OnSourceChanged(object? sender, AudioSourceChangedEventArgs args)
    {
        lock (_sync) _sourceState = args.State;
        try { SourceChanged?.Invoke(this, args); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private void OnCaptureFaulted(object? sender, AudioCaptureFaultedEventArgs args) => RaiseFaulted(args.Exception);

    private void OnClientFaulted(object? sender, Exception exception) => RaiseFaulted(exception);

    private static (float Rms, float Peak) Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return (0f, 0f);
        double sum = 0;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            sum += absolute * absolute;
            if (absolute > peak) peak = absolute;
        }

        return ((float)Math.Sqrt(sum / samples.Length), peak);
    }

    private void RaiseFaulted(Exception exception)
    {
        RaiseDiagnostic($"qwen fault {exception.GetType().Name}: {exception.Message}");
        try { Faulted?.Invoke(this, exception); }
        catch { }
    }

    private void RaiseDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(this, message); }
        catch { }
    }
}
