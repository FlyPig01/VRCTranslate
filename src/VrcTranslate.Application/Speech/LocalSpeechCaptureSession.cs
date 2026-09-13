using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Speech;

/// <summary>
/// Joins a platform audio capture stream to the local recognizer. Device
/// callbacks only perform segmentation; model inference runs asynchronously
/// behind a single gate so a slow sentence cannot reorder captions.
/// </summary>
public sealed class LocalSpeechCaptureSession : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly LocalSpeechService _speech;
    private readonly SpeechSegmenter _segmenter;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly string _sourceLanguage;
    private readonly object _sync = new();
    private bool _started;
    private bool _disposed;

    public LocalSpeechCaptureSession(
        IAudioCapture capture,
        LocalSpeechService speech,
        string sourceLanguage = "auto",
        SpeechSegmenter? segmenter = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out _sourceLanguage))
        {
            throw new ArgumentException("本地语音只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        _segmenter = segmenter ?? new SpeechSegmenter();
        _capture.SamplesReady += OnSamplesReady;
    }

    public event EventHandler<SpeechRecognitionResult>? ResultReady;

    public event EventHandler<Exception>? Faulted;

    /// <summary>Forwards measured input activity to a compact visualizer.</summary>
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }

        try
        {
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
        ThrowIfDisposed();
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
        }

        await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
        var pending = _segmenter.Flush();
        if (!pending.IsEmpty)
        {
            await RecognizeSegmentAsync(pending, cancellationToken).ConfigureAwait(false);
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
            await _capture.DisposeAsync().ConfigureAwait(false);
            _recognitionGate.Dispose();
        }
    }

    private void OnSamplesReady(object? sender, AudioSamplesEventArgs args)
    {
        if (_disposed || !IsStarted) return;
        RaiseLevelChanged(args.Samples.Span);
        if (args.SampleRate != _segmenter.SampleRate)
        {
            RaiseFaulted(new InvalidOperationException("音频采样率必须为 16 kHz。"));
            return;
        }

        foreach (var segment in _segmenter.Append(args.Samples))
        {
            _ = RecognizeSegmentAsync(segment, CancellationToken.None);
        }
    }

    private void RaiseLevelChanged(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        var sum = 0d;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            sum += absolute * absolute;
            if (absolute > peak) peak = absolute;
        }

        var rms = (float)Math.Sqrt(sum / samples.Length);
        try { LevelChanged?.Invoke(this, new AudioLevelEventArgs(rms, peak)); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private async Task RecognizeSegmentAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        try
        {
            await _recognitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _speech.RecognizeAsync(
                    new SpeechRecognitionRequest(samples, _segmenter.SampleRate, _sourceLanguage),
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Text)) RaiseResultReady(result);
            }
            finally
            {
                _recognitionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping a session is expected to cancel an in-flight segment.
        }
        catch (Exception exception)
        {
            RaiseFaulted(exception);
        }
    }

    private void RaiseResultReady(SpeechRecognitionResult result)
    {
        try { ResultReady?.Invoke(this, result); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private void RaiseFaulted(Exception exception)
    {
        try { Faulted?.Invoke(this, exception); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalSpeechCaptureSession));
    }
}
