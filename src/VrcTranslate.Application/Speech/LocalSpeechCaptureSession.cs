using System.Diagnostics;
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
    private const float AudibleRms = 0.01f;

    private readonly IAudioCapture _capture;
    private readonly LocalSpeechService _speech;
    private readonly SpeechCaptureProcessingOptions _processing;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly string _sourceLanguage;
    private readonly object _sync = new();
    private readonly object _segmenterSync = new();
    private readonly object _diagnosticsSync = new();
    private ISpeechSegmenter _segmenter;
    private int _segmenterSampleRate;
    private CancellationTokenSource _sourceCts = new();
    private AudioSourceState _sourceState;
    private long _sourceGeneration;
    private bool _started;
    private bool _disposed;
    private bool _fallbackNoticeConsumed;
    private bool _usingEnergyGate;
    private bool _splitFaultLogged;
    private long _audibleStartTimestamp;
    private long _diagnosticsWindowStart = Stopwatch.GetTimestamp();
    private long _diagnosticsChunks;
    private long _diagnosticsSegments;
    private long _diagnosticsResults;
    private long _diagnosticsEmpty;
    private float _diagnosticsMaxRms;

    public LocalSpeechCaptureSession(
        IAudioCapture capture,
        LocalSpeechService speech,
        string sourceLanguage = "auto",
        ISpeechSegmenter? segmenter = null,
        SpeechCaptureProcessingOptions? processing = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out _sourceLanguage))
        {
            throw new ArgumentException("本地语音只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        _segmenter = segmenter ?? new SpeechSegmenter();
        _segmenterSampleRate = _segmenter.SampleRate;
        // A session that was handed the managed gate by the self-test must not
        // mistake the absence of VAD segments for a broken native gate later.
        _usingEnergyGate = _segmenter is SpeechSegmenter;
        _processing = processing ?? SpeechCaptureProcessingOptions.None;
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

    /// <summary>
    /// Technical stage messages for the support log: sample counts and RMS,
    /// segment counts, recognition result counts and the reason for a fallback.
    /// Never carries recognized or translated text.
    /// </summary>
    public event EventHandler<string>? Diagnostic;

    /// <summary>
    /// Raised once when the native speech gate could not be used and the session
    /// continued with the energy gate. Captions keep flowing; the shell shows one
    /// short notice instead of an empty window.
    /// </summary>
    public event EventHandler<SpeechSegmenterFallbackEventArgs>? SegmenterFallback;

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
    /// Whether this run still owes the user its one short "only the system mix
    /// is captured" notice. True the first time the compatibility source is
    /// seen and false for every repeat, until a source other than the fallback
    /// is seen again - which is what keeps a failing activation from producing a
    /// stream of identical hints, while a genuine recovery followed by a new
    /// fallback is announced again.
    /// </summary>
    public bool ConsumeFallbackNotice()
    {
        lock (_sync)
        {
            if (!_sourceState.IsCompatibilityMode)
            {
                _fallbackNoticeConsumed = false;
                return false;
            }

            if (_fallbackNoticeConsumed) return false;
            _fallbackNoticeConsumed = true;
            return true;
        }
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
        lock (_segmenterSync)
        {
            _segmenter.Reset();
        }

        RaiseDiagnostic("source boundary: pending segment dropped");
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
        RaiseDiagnostic($"session start source={_capture.SourceKind} lang={_sourceLanguage} segmenter={_segmenter.GetType().Name}");
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
            // The first real recognition owns the user-visible error path; the
            // support log still needs to know the warm-up itself failed.
            RaiseDiagnostic($"warmup failed {exception.GetType().Name}: {exception.Message}");
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
        IReadOnlyList<ReadOnlyMemory<float>> pendingSegments;
        lock (_segmenterSync)
        {
            pendingSegments = _segmenter.Flush();
        }

        foreach (var pending in pendingSegments)
        {
            await RecognizeSegmentAsync(pending, generation, cancellationToken).ConfigureAwait(false);
        }

        var summary = $"session stop segmenter={_segmenter.GetType().Name} chunks={_diagnosticsChunks} " +
                      $"segments={_diagnosticsSegments} results={_diagnosticsResults} empty={_diagnosticsEmpty}";
        if (_segmenter is SpeechSegmenter energy)
        {
            var d = energy.Diagnostics;
            summary += $" droppedShort={d.DroppedShortSegments} noiseFloor={d.NoiseFloor:0.0000} " +
                       $"activeRatio={d.ActiveRatio:0.00}";
        }

        RaiseDiagnostic(summary);
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
            lock (_segmenterSync)
            {
                if (_segmenter is IDisposable disposableSegmenter) disposableSegmenter.Dispose();
            }

            _sourceCts.Cancel();
            _sourceCts.Dispose();
            _recognitionGate.Dispose();
        }
    }

    private void OnSamplesReady(object? sender, AudioSamplesEventArgs args)
    {
        if (_disposed || !IsStarted) return;
        var (rms, peak) = Measure(args.Samples.Span);
        RaiseLevelChanged(rms, peak);
        if (args.SampleRate != _segmenterSampleRate)
        {
            RaiseFaulted(new InvalidOperationException("音频采样率必须为 16 kHz。"));
            return;
        }

        if (!TrySnapshotSource(out var generation, out var token)) return;
        TrackAudible(rms);
        var segments = AppendSamples(args.Samples);
        if (segments.Count == 0 && ShouldFallBackForSilentVad())
        {
            SwitchToEnergyGate(new InvalidOperationException("VAD 长时间没有产出语音段（超过保护窗口）。"));
        }

        foreach (var segment in segments)
        {
            if (segment.IsEmpty) continue;
            Interlocked.Exchange(ref _audibleStartTimestamp, 0);
            Interlocked.Increment(ref _diagnosticsSegments);
            _ = RecognizeSegmentAsync(segment, generation, token);
        }

        FlushDiagnosticsIfDue(rms);
    }

    /// <summary>
    /// Feeds one captured chunk to the current gate. A native gate that throws
    /// used to lose the exception inside the capture callback: the level meter
    /// still moved, the caption window stayed empty and nothing was logged. The
    /// chunk is replayed through the energy gate so no audio is lost.
    /// </summary>
    private IReadOnlyList<ReadOnlyMemory<float>> AppendSamples(ReadOnlyMemory<float> samples)
    {
        try
        {
            lock (_segmenterSync) return _segmenter.Append(samples);
        }
        catch (Exception exception)
        {
            SwitchToEnergyGate(exception);
            try
            {
                lock (_segmenterSync) return _segmenter.Append(samples);
            }
            catch (Exception fallbackException)
            {
                RaiseFaulted(fallbackException);
                return [];
            }
        }
    }

    /// <summary>
    /// Replaces a failing or stuck speech gate with the managed energy gate for
    /// the rest of the session. Captions keep flowing; the failure is logged and
    /// surfaced once by the shell.
    /// </summary>
    private void SwitchToEnergyGate(Exception exception)
    {
        if (_usingEnergyGate) return;

        lock (_segmenterSync)
        {
            if (_usingEnergyGate || _segmenter is SpeechSegmenter)
            {
                _usingEnergyGate = true;
                return;
            }

            var previous = _segmenter;
            _segmenter = new SpeechSegmenter();
            _segmenterSampleRate = _segmenter.SampleRate;
            _usingEnergyGate = true;
            if (previous is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { /* the replacement already owns the path */ }
            }
        }

        Interlocked.Exchange(ref _audibleStartTimestamp, 0);
        RaiseDiagnostic($"segmenter fallback to energy gate: {exception.GetType().Name}: {exception.Message}");
        try
        {
            SegmenterFallback?.Invoke(
                this,
                new SpeechSegmenterFallbackEventArgs(
                    "语音活动检测长时间未产出语音段，已切换到备用分段器；字幕仍会继续。",
                    exception));
        }
        catch
        {
            // A presentation subscriber must not break the audio path.
        }
    }

    private void TrackAudible(float rms)
    {
        if (_usingEnergyGate || rms < AudibleRms) return;
        if (Interlocked.Read(ref _audibleStartTimestamp) == 0)
        {
            Interlocked.CompareExchange(ref _audibleStartTimestamp, Stopwatch.GetTimestamp(), 0);
        }
    }

    private bool ShouldFallBackForSilentVad()
    {
        if (_usingEnergyGate) return false;
        var start = Interlocked.Read(ref _audibleStartTimestamp);
        if (start == 0) return false;

        var elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed < _processing.SilentVadFallbackWindow) return false;

        // A continuous utterance longer than the native max-speech duration is
        // normal, not a stuck gate. Wait for the native max-speech cut before
        // forcing a fallback; only a gate that stays "speech detected" without
        // ever producing a segment hits the hard ceiling.
        if (_segmenter is IHasPendingSpeech { HasPendingSpeech: true } &&
            elapsed < _processing.SilentVadHardFallbackWindow)
        {
            return false;
        }

        return true;
    }

    private void FlushDiagnosticsIfDue(float rms)
    {
        Interlocked.Increment(ref _diagnosticsChunks);
        lock (_diagnosticsSync)
        {
            if (rms > _diagnosticsMaxRms) _diagnosticsMaxRms = rms;
        }

        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _diagnosticsWindowStart)) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        long chunks;
        long segments;
        long results;
        long empty;
        float maxRms;
        lock (_diagnosticsSync)
        {
            chunks = _diagnosticsChunks;
            _diagnosticsChunks = 0;
            segments = _diagnosticsSegments;
            _diagnosticsSegments = 0;
            results = _diagnosticsResults;
            _diagnosticsResults = 0;
            empty = _diagnosticsEmpty;
            _diagnosticsEmpty = 0;
            maxRms = _diagnosticsMaxRms;
            _diagnosticsMaxRms = 0f;
        }

        Interlocked.Exchange(ref _diagnosticsWindowStart, Stopwatch.GetTimestamp());
        RaiseDiagnostic(
            $"audio chunks={chunks} maxRms={maxRms:0.0000} segments={segments} " +
            $"results={results} empty={empty} gate={(_usingEnergyGate ? "energy" : "vad")}");
    }

    private static (float Rms, float Peak) Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return (0f, 0f);
        var sum = 0d;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            sum += absolute * absolute;
            if (absolute > peak) peak = absolute;
        }

        return ((float)Math.Sqrt(sum / samples.Length), peak);
    }

    private void RaiseDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(this, message); }
        catch { /* diagnostics must never break the audio path */ }
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

    private void RaiseLevelChanged(float rms, float peak)
    {
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
                    // A damaged split must cost the split, never the sentence.
                    var start = Math.Clamp(span.StartSample, 0, samples.Length);
                    var end = Math.Clamp(span.EndSample, start, samples.Length);
                    if (end <= start) continue;

                    var part = samples.Slice(start, end - start);
                    var result = await _speech.RecognizeAsync(
                        new SpeechRecognitionRequest(part, _segmenterSampleRate, _sourceLanguage),
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        var empty = Interlocked.Increment(ref _diagnosticsEmpty);
                        if (empty <= 3) RaiseDiagnostic("recognition returned no text");
                        continue;
                    }

                    // A slow result must not surface after the source switched.
                    if (IsStaleSource(generation)) return;
                    var recognized = Interlocked.Increment(ref _diagnosticsResults);
                    if (recognized <= 3)
                    {
                        RaiseDiagnostic($"result chars={result.Text.Length} lang={result.SourceLanguage}");
                    }

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
    /// The ranges to recognize separately. Splitting is the session's processing
    /// option (other-player sessions keep it on even without labels, D29); it is
    /// never gated behind a cheap "does this look like two voices" guess: that
    /// guess skipped 38% of the real speaker changes on measured dialogue while
    /// saving nothing. See docs/能力边界与决策记录.md.
    /// </summary>
    private IReadOnlyList<SpeechSpan> PlanSpeakerSpans(ReadOnlyMemory<float> samples)
    {
        var whole = new SpeechSpan(0, samples.Length);
        if (!_processing.SplitAtSpeakerChanges || !TryGetSpeakers(out var speakers)) return [whole];

        try
        {
            var spans = speakers.SplitAtSpeakerChanges(samples, _segmenterSampleRate);
            return spans.Count > 0 ? spans : [whole];
        }
        catch (Exception exception)
        {
            // Speaker separation is an enhancement: a failure must not cost the caption.
            if (!_splitFaultLogged)
            {
                _splitFaultLogged = true;
                RaiseDiagnostic($"speaker split failed: {exception.GetType().Name}: {exception.Message}");
            }

            return [whole];
        }
    }

    private SpeechRecognitionResult AttachSpeaker(
        SpeechRecognitionResult result,
        ReadOnlyMemory<float> samples)
    {
        if (!_processing.AttachSpeakerLabels || !TryGetSpeakers(out var speakers)) return result;

        var duration = samples.Length / (double)_segmenterSampleRate;
        var rms = Rms(samples.Span);
        try
        {
            // 1.1.5-compatible path: every span matches independently. No local
            // cluster prior and no previous-speaker hold; a known B can be
            // recognized from a short span directly.
            var match = speakers.Identify(samples, _segmenterSampleRate);
            if (match is null)
            {
                RaiseDiagnostic(
                    $"speaker span duration={duration:0.00} rms={rms:0.0000} decision=unknown");
                return result with
                {
                    SpeakerId = SpeakerRegistry.UnknownSpeaker.Id,
                    SpeakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName,
                };
            }

            var speaker = match.Speaker;
            RaiseDiagnostic(
                $"speaker span duration={duration:0.00} rms={rms:0.0000} " +
                $"best={match.Similarity:0.000} second={match.SecondSimilarity:0.000} " +
                $"margin={match.Margin:0.000} decision={match.Kind} " +
                $"speaker={speaker.Id}/{speaker.DisplayName}");
            return result with { SpeakerId = speaker.Id, SpeakerLabel = speaker.DisplayName };
        }
        catch (Exception exception)
        {
            RaiseDiagnostic($"speaker identify failed: {exception.GetType().Name}: {exception.Message}");
            return result;
        }
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0d;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
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
        RaiseDiagnostic($"fault {exception.GetType().Name}: {exception.Message}");
        try { Faulted?.Invoke(this, exception); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalSpeechCaptureSession));
    }
}

/// <summary>One switch from the native speech gate to the managed energy gate.</summary>
public sealed record SpeechSegmenterFallbackEventArgs(string Message, Exception? Exception);
