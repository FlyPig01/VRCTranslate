using System.Threading.Channels;
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
    private readonly ISpeechSegmenter _speakerSegmenter;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly SemaphoreSlim _fallbackGate = new(1, 1);
    private readonly SemaphoreSlim _labelGate = new(1, 1);
    private readonly SemaphoreSlim _finalGate = new(1, 1);
    private readonly SpeechSegmenter _fallbackSegmenter = new(16_000, 0.012f, 600, 6_000);
    private readonly Channel<LocalSegment> _speakerQueue = Channel.CreateUnbounded<LocalSegment>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly object _sync = new();
    private readonly object _labelsSync = new();
    private readonly List<LocalSpeakerSpan> _labels = [];
    private readonly CancellationTokenSource _sourceCts = new();
    private readonly Task _speakerWorker;
    private ITencentRealtimeRecognizer? _recognizer;
    private AudioSourceState _sourceState;
    private TaskCompletionSource _labelSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sourceGeneration;
    private long _capturedSamples;
    private long _streamOriginSample = -1;
    private long _labeledThrough;
    private long _labelGeneration;
    private bool _started;
    private bool _starting;
    private bool _disposed;
    private int _fallback;
    private int _lastLocalSpeakerId = -1;
    private string? _lastSpeakerId;
    private string? _lastSpeakerLabel;
    private string _lastPartialText = string.Empty;

    public RealtimeSpeechCaptureSession(
        IAudioCapture capture,
        LocalSpeechService speech,
        string sourceLanguage,
        ISpeechSegmenter? speakerSegmenter = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out _sourceLanguage))
        {
            throw new ArgumentException("实时识别只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        _speakerSegmenter = speakerSegmenter ?? new SpeechSegmenter(16_000, 0.012f, 600, 6_000);
        _sourceState = new AudioSourceState(_capture.SourceKind);
        _capture.SamplesReady += OnSamplesReady;
        _capture.SourceChanged += OnSourceChanged;
        _capture.Faulted += OnCaptureFaulted;
        _capture.Stopped += OnCaptureStopped;
        _speakerWorker = Task.Run(() => SpeakerLoopAsync(_sourceCts.Token));
    }

    public event EventHandler<SpeechRecognitionResult>? ResultReady;

    public event EventHandler<TencentRealtimePartial>? PartialReady;

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
        ResetLocalLabels();
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

        ClearPartial();
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
            recognizer.PartialReady -= OnPartialReady;
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

        ResetLocalLabels();
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
            if (_speakerSegmenter is IDisposable disposableSegmenter) disposableSegmenter.Dispose();
            _sourceCts.Cancel();
            _speakerQueue.Writer.TryComplete();
            try { await _speakerWorker.ConfigureAwait(false); }
            catch { /* the worker is best-effort */ }
            _sourceCts.Dispose();
            _restartGate.Dispose();
            _fallbackGate.Dispose();
            _labelGate.Dispose();
            _finalGate.Dispose();
        }
    }

    private async Task StartRecognizerAsync(CancellationToken cancellationToken)
    {
        var recognizer = _speech.CreateTencentRealtime()
            ?? throw new InvalidOperationException("腾讯云实时识别尚未配置。");
        var generation = Interlocked.Increment(ref _sourceGeneration);
        recognizer.ResultReady += OnResultReady;
        recognizer.PartialReady += OnPartialReady;
        recognizer.Faulted += OnRecognizerFaulted;
        if (Interlocked.CompareExchange(ref _recognizer, recognizer, null) is not null)
        {
            // A source switch raced this start; the newer one owns the field.
            recognizer.ResultReady -= OnResultReady;
            recognizer.PartialReady -= OnPartialReady;
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
                recognizer.PartialReady -= OnPartialReady;
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

        var endTotal = Interlocked.Add(ref _capturedSamples, args.Samples.Length);
        try
        {
            if (Volatile.Read(ref _fallback) == 1)
            {
                FeedFallback(args.Samples);
                return;
            }

            var recognizer = Volatile.Read(ref _recognizer);
            if (recognizer is null) return;
            if (Interlocked.Read(ref _streamOriginSample) < 0)
            {
                Interlocked.CompareExchange(ref _streamOriginSample, endTotal - args.Samples.Length, -1);
            }

            recognizer.Append(args.Samples, args.SampleRate);
            FeedSpeakerSegments(args.Samples, endTotal);
        }
        catch (Exception exception)
        {
            RaiseFaulted(exception);
        }
    }

    private void FeedSpeakerSegments(ReadOnlyMemory<float> samples, long endTotal)
    {
        var segments = _speakerSegmenter.Append(samples);
        if (segments.Count == 0) return;
        var cursor = endTotal - segments.Sum(segment => segment.Length);
        var generation = Interlocked.Read(ref _labelGeneration);
        foreach (var segment in segments)
        {
            if (segment.IsEmpty) continue;
            var start = Math.Max(0, cursor);
            cursor += segment.Length;
            _speakerQueue.Writer.TryWrite(new LocalSegment(segment, start, cursor, generation));
        }
    }

    private void OnPartialReady(object? sender, TencentRealtimePartial partial)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _recognizer))) return;
        if (Volatile.Read(ref _fallback) == 1 || !IsStarted) return;
        if (string.IsNullOrWhiteSpace(partial.Text)) return;
        if (string.Equals(partial.Text, _lastPartialText, StringComparison.Ordinal)) return;
        _lastPartialText = partial.Text;
        try { PartialReady?.Invoke(this, partial); }
        catch (Exception exception) { RaiseFaulted(exception); }
    }

    private void ClearPartial()
    {
        if (string.IsNullOrEmpty(_lastPartialText)) return;
        _lastPartialText = string.Empty;
        try { PartialReady?.Invoke(this, new TencentRealtimePartial(string.Empty, -1, null)); }
        catch { /* clearing a preview must never break the session */ }
    }

    private void OnResultReady(object? sender, TencentRealtimeResult result)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _recognizer))) return;
        if (Volatile.Read(ref _fallback) == 1) return;
        if (!IsStarted || string.IsNullOrWhiteSpace(result.Text)) return;
        _ = HandleFinalAsync(result);
    }

    private async Task HandleFinalAsync(TencentRealtimeResult result)
    {
        try
        {
            await _finalGate.WaitAsync(_sourceCts.Token).ConfigureAwait(false);
            try
            {
                if (!IsStarted || Volatile.Read(ref _fallback) == 1) return;
                var origin = Interlocked.Read(ref _streamOriginSample);
                long startSample = -1;
                long endSample = -1;
                if (origin >= 0)
                {
                    startSample = origin + (long)(result.Start.TotalMilliseconds * SampleRate / 1000d);
                    endSample = origin + (long)(result.End.TotalMilliseconds * SampleRate / 1000d);
                    await WaitForLabelsAsync(endSample, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                }

                var labels = SnapshotLabels();
                var outputs = BuildResults(result, origin, startSample, endSample, labels);
                ClearPartial();
                foreach (var recognized in outputs)
                {
                    try { ResultReady?.Invoke(this, recognized); }
                    catch (Exception exception) { RaiseFaulted(exception); }
                }
            }
            finally
            {
                _finalGate.Release();
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

    private IReadOnlyList<SpeechRecognitionResult> BuildResults(
        TencentRealtimeResult result,
        long origin,
        long startSample,
        long endSample,
        IReadOnlyList<LocalSpeakerSpan> labels)
    {
        var whole = new SpeechRecognitionResult(
            result.MessageId ?? Guid.NewGuid().ToString("N"),
            result.Text.Trim(),
            _sourceLanguage,
            result.End > result.Start ? result.End - result.Start : TimeSpan.Zero);

        if (labels.Count == 0 || origin < 0 || endSample <= startSample)
        {
            return [whole with
            {
                SpeakerId = SpeakerRegistry.UnknownSpeaker.Id,
                SpeakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName,
            }];
        }

        var spans = labels
            .Where(span => span.EndSample > startSample && span.StartSample < endSample)
            .OrderBy(span => span.StartSample)
            .ToArray();
        if (spans.Length == 0)
        {
            return [whole with
            {
                SpeakerId = SpeakerRegistry.UnknownSpeaker.Id,
                SpeakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName,
            }];
        }

        if (result.Words is not { Count: > 0 } words)
        {
            var best = spans
                .OrderByDescending(span => Math.Min(span.EndSample, endSample) - Math.Max(span.StartSample, startSample))
                .First();
            return [whole with { SpeakerId = best.SpeakerId, SpeakerLabel = best.SpeakerLabel }];
        }

        var outputs = new List<SpeechRecognitionResult>();
        foreach (var span in spans)
        {
            var spanStartMs = (span.StartSample - origin) * 1000d / SampleRate;
            var spanEndMs = (span.EndSample - origin) * 1000d / SampleRate;
            var text = JoinWords(words.Where(word =>
            {
                var midpoint = (word.Start + word.End).TotalMilliseconds / 2d;
                return midpoint >= spanStartMs && midpoint < spanEndMs;
            }));
            if (string.IsNullOrWhiteSpace(text)) continue;

            outputs.Add(whole with
            {
                RequestId = result.MessageId is null
                    ? Guid.NewGuid().ToString("N")
                    : $"{result.MessageId}-{span.StartSample}",
                Text = text,
                Elapsed = TimeSpan.FromMilliseconds(Math.Max(0, spanEndMs - spanStartMs)),
                SpeakerId = span.SpeakerId,
                SpeakerLabel = span.SpeakerLabel,
            });
        }

        if (outputs.Count > 0) return outputs;
        var fallback = spans
            .OrderByDescending(span => Math.Min(span.EndSample, endSample) - Math.Max(span.StartSample, startSample))
            .First();
        return [whole with { SpeakerId = fallback.SpeakerId, SpeakerLabel = fallback.SpeakerLabel }];
    }

    private IReadOnlyList<LocalSpeakerSpan> SnapshotLabels()
    {
        lock (_labelsSync) return [.. _labels];
    }

    private async Task WaitForLabelsAsync(long endSample, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (_labelsSync)
            {
                if (_labeledThrough >= endSample) return;
                wait = _labelSignal.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            try
            {
                await wait.WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private async Task SpeakerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _speakerQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _labelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    LabelSegment(segment);
                }
                catch (Exception exception)
                {
                    RaiseDiagnostic(
                        $"realtime local label failed: {exception.GetType().Name}: {exception.Message}");
                    MarkLabeled(segment.EndSample);
                }
                finally
                {
                    _labelGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void LabelSegment(LocalSegment segment)
    {
        if (segment.Generation != Interlocked.Read(ref _labelGeneration)) return;
        if (_speech.Speakers is not { IsAvailable: true } speakers)
        {
            MarkLabeled(segment.EndSample);
            return;
        }

        IReadOnlyList<SpeechSpan> spans;
        try
        {
            spans = speakers.SplitAtSpeakerChanges(segment.Samples, SampleRate);
        }
        catch (Exception exception)
        {
            RaiseDiagnostic(
                $"realtime local split failed: {exception.GetType().Name}: {exception.Message}");
            MarkLabeled(segment.EndSample);
            return;
        }

        if (spans.Count == 0) spans = [new SpeechSpan(0, segment.Samples.Length)];
        _lastLocalSpeakerId = -1;
        foreach (var span in spans)
        {
            var start = Math.Clamp(span.StartSample, 0, segment.Samples.Length);
            var end = Math.Clamp(span.EndSample, start, segment.Samples.Length);
            if (end <= start) continue;

            var part = segment.Samples.Slice(start, end - start);
            var duration = (end - start) / (double)SampleRate;
            var sameLocal = span.LocalSpeakerId >= 0 && span.LocalSpeakerId == _lastLocalSpeakerId;
            var allowCreate = duration >= 1.5 && !sameLocal;
            var match = speakers.Identify(part, SampleRate, allowCreate: allowCreate);
            var speakerId = SpeakerRegistry.UnknownSpeaker.Id;
            var speakerLabel = SpeakerRegistry.UnknownSpeaker.DisplayName;
            var decision = "unknown";

            if (match is null)
            {
                if (duration >= 1.5 && sameLocal && _lastSpeakerId is not null)
                {
                    speakerId = _lastSpeakerId;
                    speakerLabel = _lastSpeakerLabel ?? SpeakerRegistry.UnknownSpeaker.DisplayName;
                    decision = "local-hold-null";
                }
            }
            else if (duration < 1.5 && match.Kind is SpeakerMatchKind.New or SpeakerMatchKind.Unknown)
            {
                if (match.Kind == SpeakerMatchKind.New)
                {
                    try { speakers.Forget(match.Speaker.Id); }
                    catch { /* the unknown label is still correct */ }
                }

                decision = "short-unknown";
            }
            else if (sameLocal && _lastSpeakerId is not null &&
                     match.Kind is SpeakerMatchKind.New or SpeakerMatchKind.Unknown)
            {
                if (match.Kind == SpeakerMatchKind.New)
                {
                    try { speakers.Forget(match.Speaker.Id); }
                    catch { /* the label can still hold */ }
                }

                speakerId = _lastSpeakerId;
                speakerLabel = _lastSpeakerLabel ?? SpeakerRegistry.UnknownSpeaker.DisplayName;
                decision = "local-hold";
            }
            else
            {
                speakerId = match.Speaker.Id;
                speakerLabel = match.Speaker.DisplayName;
                decision = match.Kind.ToString();
            }

            if (speakerId == SpeakerRegistry.UnknownSpeaker.Id)
            {
                _lastSpeakerId = null;
                _lastSpeakerLabel = null;
            }
            else
            {
                _lastSpeakerId = speakerId;
                _lastSpeakerLabel = speakerLabel;
                _lastLocalSpeakerId = span.LocalSpeakerId;
            }

            if (segment.Generation != Interlocked.Read(ref _labelGeneration)) return;
            lock (_labelsSync)
            {
                if (segment.Generation != _labelGeneration) return;
                _labels.Add(new LocalSpeakerSpan(
                    segment.StartSample + start,
                    segment.StartSample + end,
                    speakerId,
                    speakerLabel));
            }

            RaiseDiagnostic(
                $"realtime local speaker duration={duration:0.00} decision={decision} " +
                $"speaker={speakerId}/{speakerLabel} local={span.LocalSpeakerId}");
        }

        MarkLabeled(segment.EndSample);
    }

    private void MarkLabeled(long endSample)
    {
        TaskCompletionSource signal;
        lock (_labelsSync)
        {
            if (endSample > _labeledThrough) _labeledThrough = endSample;
            var oldest = Interlocked.Read(ref _capturedSamples) - SampleRate * 60L;
            _labels.RemoveAll(span => span.EndSample < oldest);
            signal = _labelSignal;
            _labelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
    }

    private void ResetLocalLabels()
    {
        Interlocked.Increment(ref _labelGeneration);
        _speakerSegmenter.Reset();
        _lastLocalSpeakerId = -1;
        _lastSpeakerId = null;
        _lastSpeakerLabel = null;
        Interlocked.Exchange(ref _streamOriginSample, -1);
        TaskCompletionSource signal;
        lock (_labelsSync)
        {
            _labels.Clear();
            _labeledThrough = 0;
            signal = _labelSignal;
            _labelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
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
        ClearPartial();
        ResetLocalLabels();
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
        ClearPartial();
        ResetLocalLabels();
        _fallbackSegmenter.Reset();
        if (Volatile.Read(ref _fallback) == 1) return;

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
                old.PartialReady -= OnPartialReady;
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

    private sealed record LocalSegment(
        ReadOnlyMemory<float> Samples,
        long StartSample,
        long EndSample,
        long Generation);

    private sealed record LocalSpeakerSpan(long StartSample, long EndSample, string SpeakerId, string SpeakerLabel);
}
