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
    private readonly ISpeechSegmenter _segmenter;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly string _sourceLanguage;
    private readonly object _sync = new();
    private CancellationTokenSource _sourceCts = new();
    private AudioSourceState _sourceState;
    private long _sourceGeneration;
    private bool _started;
    private bool _disposed;

    public LocalSpeechCaptureSession(
        IAudioCapture capture,
        LocalSpeechService speech,
        string sourceLanguage = "auto",
        ISpeechSegmenter? segmenter = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out _sourceLanguage))
        {
            throw new ArgumentException("本地语音只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        _segmenter = segmenter ?? new SpeechSegmenter();
        _sourceState = new AudioSourceState(_capture.SourceKind);
        _capture.SamplesReady += OnSamplesReady;
        _capture.SourceChanged += OnSourceChanged;
    }

    public event EventHandler<SpeechRecognitionResult>? ResultReady;

    public event EventHandler<Exception>? Faulted;

    /// <summary>Forwards capture source changes so the shell can label the input.</summary>
    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    /// <summary>Forwards measured input activity to a compact visualizer.</summary>
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public bool IsStarted
    {
        get { lock (_sync) return _started; }
    }

    /// <summary>Source the recognizer is currently fed from.</summary>
    public AudioSourceState SourceState
    {
        get { lock (_sync) return _sourceState; }
    }

    /// <summary>
    /// Source boundary entry point: the unclosed segment is dropped and
    /// recognition started for the previous source is cancelled, so a late
    /// result can never be published as if it came from the new source. The
    /// capture raises it through <see cref="IAudioCapture.SourceChanged"/>.
    /// </summary>
    public void ResetForSourceBoundary()
    {
        CancellationTokenSource? retired;
        lock (_sync)
        {
            if (_disposed) return;
            _sourceGeneration++;
            retired = _sourceCts;
            _sourceCts = new CancellationTokenSource();
        }

        // A recognition may still be running on the retired token, so it is
        // cancelled but not disposed: a CancellationTokenSource without timers
        // owns no unmanaged handle and is collected together with its tasks.
        try { retired.Cancel(); }
        catch (ObjectDisposedException) { }
        _segmenter.Reset();
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

        // Loading the local model costs an order of magnitude more than one
        // recognition, so the session loads it up front instead of making the
        // first sentence pay for it.
        _ = WarmUpAsync();
    }

    private async Task WarmUpAsync()
    {
        try
        {
            await _speech.PrepareAsync(_sourceLanguage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The first real recognition owns the user-visible error path.
            _ = exception;
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
        // The flushed tail belongs to the source that is still current, so it
        // keeps the stop-time behaviour of publishing the last half sentence.
        var generation = CurrentSourceGeneration();
        foreach (var pending in _segmenter.Flush())
        {
            await RecognizeSegmentAsync(pending, generation, cancellationToken).ConfigureAwait(false);
        }
        if (_segmenter is SpeechSegmenter energy)
        {
            // One line per stop so a missing-caption session can be described
            // from a debug log without adding UI for it.
            var d = energy.Diagnostics;
            System.Diagnostics.Debug.WriteLine(
                $"[speech] segments={d.CompletedSegments} droppedShort={d.DroppedShortSegments} " +
                $"noiseFloor={d.NoiseFloor:0.0000} activeRatio={d.ActiveRatio:0.00}");
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
            await _capture.DisposeAsync().ConfigureAwait(false);
            if (_segmenter is IDisposable disposableSegmenter) disposableSegmenter.Dispose();
            _sourceCts.Cancel();
            _sourceCts.Dispose();
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

        if (!TrySnapshotSource(out var generation, out var token)) return;
        foreach (var segment in _segmenter.Append(args.Samples))
        {
            _ = RecognizeSegmentAsync(segment, generation, token);
        }
    }

    private void OnSourceChanged(object? sender, AudioSourceChangedEventArgs args)
    {
        lock (_sync) _sourceState = args.State;
        if (args.IsBoundary) ResetForSourceBoundary();
        try { SourceChanged?.Invoke(this, args); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    /// <summary>Generation and cancellation token of the source that is fed right now.</summary>
    private bool TrySnapshotSource(out long generation, out CancellationToken token)
    {
        lock (_sync)
        {
            generation = _sourceGeneration;
            try
            {
                token = _sourceCts.Token;
                return !token.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                token = CancellationToken.None;
                return false;
            }
        }
    }

    private long CurrentSourceGeneration()
    {
        lock (_sync) return _sourceGeneration;
    }

    private bool IsStaleSource(long generation)
    {
        lock (_sync) return generation != _sourceGeneration;
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

    private async Task RecognizeSegmentAsync(
        ReadOnlyMemory<float> samples,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            // Work queued for a source that is already gone is never started.
            if (IsStaleSource(generation)) return;
            await _recognitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsStaleSource(generation)) return;
                // One span normally; two or three when one sentence holds two voices.
                foreach (var span in PlanSpeakerSpans(samples))
                {
                    var part = samples.Slice(span.StartSample, span.Length);
                    var result = await _speech.RecognizeAsync(
                        new SpeechRecognitionRequest(part, _segmenter.SampleRate, _sourceLanguage),
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(result.Text)) continue;
                    // A slow result must not surface after the source switched.
                    if (IsStaleSource(generation)) return;
                    RaiseResultReady(AttachSpeaker(result, part));
                }
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

    /// <summary>
    /// The ranges to recognize separately. Splitting costs two extra embeddings and
    /// a segmentation pass, so it is only attempted when speaker labels are on and
    /// the cheap head/tail comparison already suspects two voices.
    /// </summary>
    private IReadOnlyList<SpeechSpan> PlanSpeakerSpans(ReadOnlyMemory<float> samples)
    {
        var whole = new SpeechSpan(0, samples.Length);
        if (!_speech.SpeakerLabelsEnabled || !TryGetSpeakers(out var speakers)) return [whole];

        try
        {
            if (!speakers.IsSpeakerChangeSuspected(samples, _segmenter.SampleRate)) return [whole];
            var spans = speakers.SplitAtSpeakerChanges(samples, _segmenter.SampleRate);
            return spans.Count > 1 ? spans : [whole];
        }
        catch (Exception exception)
        {
            // Speaker separation is an enhancement: a failure must not cost the caption.
            _ = exception;
            return [whole];
        }
    }

    private SpeechRecognitionResult AttachSpeaker(SpeechRecognitionResult result, ReadOnlyMemory<float> samples)
    {
        if (!_speech.SpeakerLabelsEnabled || !TryGetSpeakers(out var speakers)) return result;

        try
        {
            var match = speakers.Identify(samples, _segmenter.SampleRate);
            return match is null
                ? result
                : result with { SpeakerId = match.Speaker.Id, SpeakerLabel = match.Speaker.DisplayName };
        }
        catch (Exception exception)
        {
            _ = exception;
            return result;
        }
    }

    private bool TryGetSpeakers(out ISpeakerIdentifier speakers)
    {
        speakers = _speech.Speakers!;
        try
        {
            return speakers is { IsAvailable: true };
        }
        catch (Exception exception)
        {
            _ = exception;
            return false;
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
