using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Speech;

/// <summary>
/// Feeds the loopback stream to one Tencent real-time connection and turns its
/// stable sentences into the same <see cref="SpeechRecognitionResult"/> values
/// the local session produces. The local VAD is intentionally not used here:
/// the cloud engine owns sentence boundaries, while speaker labels are attached
/// from the audio interval returned with each stable sentence.
/// </summary>
public sealed class RealtimeSpeechCaptureSession : ISpeechCaptureSession
{
    private const int SampleRate = 16_000;

    private readonly IAudioCapture _capture;
    private readonly LocalSpeechService _speech;
    private readonly string _sourceLanguage;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly SemaphoreSlim _fallbackGate = new(1, 1);
    private readonly SpeechSegmenter _fallbackSegmenter = new(16_000, 0.012f, 600, 6_000);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _sourceCts = new();
    private ITencentRealtimeRecognizer? _recognizer;
    private AudioSourceState _sourceState;
    private long _sourceGeneration;
    private bool _started;
    private bool _starting;
    private bool _disposed;
    private int _fallback;
    private string? _lastSpeakerId;
    private string? _lastSpeakerLabel;

    public RealtimeSpeechCaptureSession(
        IAudioCapture capture,
        LocalSpeechService speech,
        string sourceLanguage)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out _sourceLanguage))
        {
            throw new ArgumentException("实时识别只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        _sourceState = new AudioSourceState(_capture.SourceKind);
        _capture.SamplesReady += OnSamplesReady;
        _capture.SourceChanged += OnSourceChanged;
        _capture.Faulted += OnCaptureFaulted;
        _capture.Stopped += OnCaptureStopped;
    }

    public event EventHandler<SpeechRecognitionResult>? ResultReady;

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public event EventHandler<string>? Diagnostic;

    /// <summary>Real-time recognition has no local segmenter to fall back from.</summary>
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
            if (_disposed) throw new ObjectDisposedException(nameof(RealtimeSpeechCaptureSession));
            if (_started) return;
            _started = true;
        }

        _starting = true;
        try
        {
            await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StartRecognizerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                EnableFallback(exception);
            }

            RaiseDiagnostic("realtime session started");
        }
        catch
        {
            lock (_sync) _started = false;
            throw;
        }
        finally
        {
            _starting = false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_started) return;
        }

        await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _fallback) == 1)
        {
            var generation = Interlocked.Read(ref _sourceGeneration);
            foreach (var segment in _fallbackSegmenter.Flush())
            {
                if (segment.IsEmpty) continue;
                await RecognizeFallbackAsync(segment, generation).ConfigureAwait(false);
            }
        }

        var recognizer = Interlocked.Exchange(ref _recognizer, null);
        if (recognizer is not null)
        {
            recognizer.ResultReady -= OnResultReady;
            recognizer.Faulted -= OnRecognizerFaulted;
            try
            {
                await recognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // The connection may already be gone; local fallback already owns the session.
            }
            finally
            {
                await recognizer.DisposeAsync().ConfigureAwait(false);
            }
        }

        lock (_sync) _started = false;
        RaiseDiagnostic("realtime session stopped");
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
            _capture.Stopped -= OnCaptureStopped;
            await _capture.DisposeAsync().ConfigureAwait(false);
            _sourceCts.Cancel();
            _sourceCts.Dispose();
            _restartGate.Dispose();
            _fallbackGate.Dispose();
        }
    }

    private async Task StartRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = _speech.CreateTencentRealtime()
            ?? throw new InvalidOperationException("腾讯云实时识别尚未配置。");
        var generation = Interlocked.Increment(ref _sourceGeneration);
        recognizer.ResultReady += OnResultReady;
        recognizer.Faulted += OnRecognizerFaulted;
        if (Interlocked.CompareExchange(ref _recognizer, recognizer, null) is not null)
        {
            // A source switch raced this start; the newer one owns the field.
            recognizer.ResultReady -= OnResultReady;
            recognizer.Faulted -= OnRecognizerFaulted;
            await recognizer.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await recognizer.StartAsync(cancellationToken).ConfigureAwait(false);
            RaiseDiagnostic($"realtime connected lang={_sourceLanguage} gen={generation}");
        }
        catch
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _recognizer, null, recognizer), recognizer))
            {
                recognizer.ResultReady -= OnResultReady;
                recognizer.Faulted -= OnRecognizerFaulted;
                await recognizer.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private void OnSamplesReady(object? sender, AudioSamplesEventArgs args)
    {
        if (_disposed || !IsStarted) return;
        var (rms, peak) = Measure(args.Samples.Span);
        try { LevelChanged?.Invoke(this, new AudioLevelEventArgs(rms, peak)); }
        catch (Exception exception) { RaiseFaulted(exception); }

        if (args.SampleRate != SampleRate)
        {
            RaiseFaulted(new InvalidOperationException("音频采样率必须为 16 kHz。"));
            return;
        }

        try
        {
            if (Volatile.Read(ref _fallback) == 1)
            {
                FeedFallback(args.Samples);
            }
            else
            {
                Volatile.Read(ref _recognizer)?.Append(args.Samples, args.SampleRate);
            }
        }
        catch (Exception exception)
        {
            RaiseFaulted(exception);
        }
    }

    private void OnResultReady(object? sender, TencentRealtimeResult result)
    {
        if (Volatile.Read(ref _fallback) == 1) return;
        if (!IsStarted || string.IsNullOrWhiteSpace(result.Text)) return;
        foreach (var recognized in BuildResults(result))
        {
            try { ResultReady?.Invoke(this, recognized); }
            catch (Exception exception) { RaiseFaulted(exception); }
        }
    }

    private IReadOnlyList<SpeechRecognitionResult> BuildResults(TencentRealtimeResult result)
    {
        var whole = new SpeechRecognitionResult(
            result.MessageId ?? Guid.NewGuid().ToString("N"),
            result.Text.Trim(),
            _sourceLanguage,
            result.End > result.Start ? result.End - result.Start : TimeSpan.Zero);

        if (result.Audio.IsEmpty ||
            result.Words is not { Count: > 0 } words ||
            _speech.Speakers is not { IsAvailable: true } speakers)
        {
            return [AttachSpeaker(whole, result.Audio)];
        }

        IReadOnlyList<SpeechSpan> spans;
        try
        {
            spans = speakers.SplitAtSpeakerChanges(result.Audio, SampleRate);
        }
        catch (Exception exception)
        {
            RaiseDiagnostic($"realtime speaker split failed: {exception.GetType().Name}: {exception.Message}");
            return [AttachSpeaker(whole, result.Audio)];
        }

        if (spans.Count <= 1) return [AttachSpeaker(whole, result.Audio)];

        var results = new List<SpeechRecognitionResult>();
        foreach (var span in spans)
        {
            var start = Math.Clamp(span.StartSample, 0, result.Audio.Length);
            var end = Math.Clamp(span.EndSample, start, result.Audio.Length);
            if (end <= start) continue;

            var spanStartMs = result.Start.TotalMilliseconds + start * 1000d / SampleRate;
            var spanEndMs = result.Start.TotalMilliseconds + end * 1000d / SampleRate;
            var text = JoinWords(words.Where(word =>
            {
                var midpoint = (word.Start + word.End).TotalMilliseconds / 2d;
                return midpoint >= spanStartMs && midpoint < spanEndMs;
            }));
            if (string.IsNullOrWhiteSpace(text)) continue;

            var part = result.Audio.Slice(start, end - start);
            var recognized = whole with
            {
                RequestId = result.MessageId is null ? Guid.NewGuid().ToString("N") : result.MessageId + "-" + span.StartSample,
                Text = text,
                Elapsed = TimeSpan.FromMilliseconds(spanEndMs - spanStartMs),
            };
            results.Add(AttachSpeaker(recognized, part));
        }

        return results.Count > 0 ? results : [AttachSpeaker(whole, result.Audio)];
    }

    private string JoinWords(IEnumerable<TencentRealtimeWord> words)
    {
        var text = string.Join(_sourceLanguage == "en" ? " " : string.Empty, words.Select(word => word.Text.Trim()));
        return text.Trim();
    }

    private SpeechRecognitionResult AttachSpeaker(SpeechRecognitionResult result, ReadOnlyMemory<float> audio)
    {
        if (audio.IsEmpty || _speech.Speakers is not { IsAvailable: true } speakers) return result;

        var duration = audio.Length / (double)SampleRate;
        try
        {
            var match = speakers.Identify(audio, SampleRate, allowCreate: duration >= 1.5);
            if (match is null)
            {
                if (duration < 1.5 && _lastSpeakerId is not null)
                {
                    RaiseDiagnostic(
                        $"realtime speaker duration={duration:0.00} decision=hold speaker={_lastSpeakerId}");
                    return result with { SpeakerId = _lastSpeakerId, SpeakerLabel = _lastSpeakerLabel };
                }

                RaiseDiagnostic($"realtime speaker duration={duration:0.00} decision=unknown");
                return result with
                {
                    SpeakerId = SpeakerRegistry.UnknownSpeaker.Id,
                    SpeakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName,
                };
            }

            if (duration < 1.5 && match.Kind is SpeakerMatchKind.New or SpeakerMatchKind.Unknown)
            {
                if (match.Kind == SpeakerMatchKind.New)
                {
                    try { speakers.Forget(match.Speaker.Id); }
                    catch { /* the unknown label is still correct */ }
                }

                return result with
                {
                    SpeakerId = SpeakerRegistry.UnknownSpeaker.Id,
                    SpeakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName,
                };
            }

            if (match.Speaker.Id == SpeakerRegistry.UnknownSpeaker.Id)
            {
                _lastSpeakerId = null;
                _lastSpeakerLabel = null;
            }
            else
            {
                _lastSpeakerId = match.Speaker.Id;
                _lastSpeakerLabel = match.Speaker.DisplayName;
            }

            RaiseDiagnostic(
                $"realtime speaker duration={duration:0.00} best={match.Similarity:0.000} " +
                $"decision={match.Kind} speaker={match.Speaker.Id}/{match.Speaker.DisplayName}");
            return result with
            {
                SpeakerId = match.Speaker.Id,
                SpeakerLabel = match.Speaker.DisplayName,
            };
        }
        catch (Exception exception)
        {
            RaiseDiagnostic($"realtime speaker identify failed: {exception.GetType().Name}: {exception.Message}");
            return result;
        }
    }

    private void FeedFallback(ReadOnlyMemory<float> samples)
    {
        var generation = Interlocked.Read(ref _sourceGeneration);
        foreach (var segment in _fallbackSegmenter.Append(samples))
        {
            if (segment.IsEmpty) continue;
            _ = RecognizeFallbackAsync(segment, generation);
        }
    }

    private async Task RecognizeFallbackAsync(ReadOnlyMemory<float> samples, long generation)
    {
        try
        {
            await _fallbackGate.WaitAsync(_sourceCts.Token).ConfigureAwait(false);
            try
            {
                if (!IsStarted || generation != Interlocked.Read(ref _sourceGeneration)) return;
                var result = await _speech.RecognizeAsync(
                    new SpeechRecognitionRequest(samples, SampleRate, _sourceLanguage),
                    useCloudAsr: false,
                    _sourceCts.Token).ConfigureAwait(false);
                if (!IsStarted || string.IsNullOrWhiteSpace(result.Text)) return;
                if (generation != Interlocked.Read(ref _sourceGeneration)) return;
                var withSpeaker = AttachSpeaker(result, samples);
                try { ResultReady?.Invoke(this, withSpeaker); }
                catch (Exception exception) { RaiseFaulted(exception); }
            }
            finally
            {
                _fallbackGate.Release();
            }
        }
        catch (OperationCanceledException) when (_sourceCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseFaulted(exception);
        }
    }

    private void EnableFallback(Exception exception)
    {
        if (Interlocked.Exchange(ref _fallback, 1) == 1) return;
        _fallbackSegmenter.Reset();
        _speech.NotifyCloudFallback(exception.Message);
        RaiseDiagnostic($"realtime unavailable, local fallback: {exception.GetType().Name}: {exception.Message}");
    }

    private void OnSourceChanged(object? sender, AudioSourceChangedEventArgs args)
    {
        lock (_sync) _sourceState = args.State;
        try { SourceChanged?.Invoke(this, args); }
        catch (Exception exception) { RaiseFaulted(exception); }
        if (!args.IsBoundary || !IsStarted || _starting) return;
        if (Volatile.Read(ref _fallback) == 1)
        {
            _fallbackSegmenter.Reset();
            _lastSpeakerId = null;
            _lastSpeakerLabel = null;
            return;
        }

        _ = RestartRecognizerAsync();
    }

    private async Task RestartRecognizerAsync()
    {
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsStarted) return;
            var old = Interlocked.Exchange(ref _recognizer, null);
            if (old is not null)
            {
                old.ResultReady -= OnResultReady;
                old.Faulted -= OnRecognizerFaulted;
                try { await old.CompleteAsync(_sourceCts.Token).ConfigureAwait(false); }
                catch { /* the source is gone; closing is best-effort */ }
                await old.DisposeAsync().ConfigureAwait(false);
            }

            _lastSpeakerId = null;
            _lastSpeakerLabel = null;
            await StartRecognizerAsync(_sourceCts.Token).ConfigureAwait(false);
            RaiseDiagnostic("realtime recognizer restarted for source boundary");
        }
        catch (Exception exception)
        {
            RaiseFaulted(exception);
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private void OnRecognizerFaulted(object? sender, Exception exception) => EnableFallback(exception);

    private void OnCaptureFaulted(object? sender, AudioCaptureFaultedEventArgs args) => RaiseFaulted(args.Exception);

    private void OnCaptureStopped(object? sender, AudioCaptureStoppedEventArgs args)
    {
        if (args.Exception is not null) RaiseFaulted(args.Exception);
        RaiseDiagnostic("realtime capture stopped");
    }

    private static (float Rms, float Peak) Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return (0f, 0f);
        double sum = 0d;
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

    private void RaiseFaulted(Exception exception)
    {
        RaiseDiagnostic($"realtime fault {exception.GetType().Name}: {exception.Message}");
        try { Faulted?.Invoke(this, exception); }
        catch { }
    }
}
