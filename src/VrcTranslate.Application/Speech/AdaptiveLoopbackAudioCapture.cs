using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Application.Speech;

/// <summary>Product policy for <see cref="AdaptiveLoopbackAudioCapture"/>.</summary>
public sealed class AdaptiveLoopbackOptions
{
    /// <summary>
    /// Delay before the next activation attempt for the same failing process
    /// identity. The last entry caps every further retry.
    /// </summary>
    public IReadOnlyList<TimeSpan> FailureBackoff { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Delay hook; tests replace it so a 30 second backoff does not slow the suite.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public static AdaptiveLoopbackOptions Default { get; } = new();
}

/// <summary>
/// Application-level coordinator that presents system loopback, VRChat process
/// loopback and the compatibility fallback as one continuous capture stream.
/// The platform monitor pushes decisions into <see cref="ApplyTargetAsync"/>,
/// which never polls: this class owns the switching state machine, the
/// generation counter and the failure backoff, so the desktop session only ever
/// observes source boundaries.
/// </summary>
/// <remarks>
/// The events are raised from the thread that applies a decision, and only
/// while the capture instance was already replaced. Subscribers must return
/// quickly and must not call back into this capture synchronously, because the
/// transition gate is still held; the desktop session only resets its segmenter
/// and forwards the state, which is exactly that shape.
/// </remarks>
public sealed class AdaptiveLoopbackAudioCapture : IAudioCapture
{
    private const int SourceSampleRate = 16_000;

    private readonly IAudioCaptureFactory _factory;
    private readonly AdaptiveLoopbackOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _dispatchSync = new();
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeAttempt;
    private IAudioCapture? _current;
    private AudioSourceState _state = AudioSourceState.SystemLoopback;
    private ProcessIdentity? _target;
    private ProcessIdentity? _failedTarget;
    private int _failureCount;
    private long _generation;
    private bool _backoffPending;
    private bool _processLoopbackUnavailable;
    private bool _started;
    private bool _stoppedPublished;
    private bool _disposed;
    private bool _dispatching = true;

    public AdaptiveLoopbackAudioCapture(IAudioCaptureFactory factory, AdaptiveLoopbackOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? AdaptiveLoopbackOptions.Default;
    }

    public AudioCaptureMode Mode => AudioCaptureMode.SystemLoopback;

    public int SampleRate => SourceSampleRate;

    public AudioCaptureSourceKind SourceKind
    {
        get { lock (_sync) return _state.Kind; }
    }

    /// <summary>Latest published source state; the status badge mirrors this value.</summary>
    public AudioSourceState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>The identity the coordinator currently follows, or null when VRChat is away.</summary>
    public ProcessIdentity? Target
    {
        get { lock (_sync) return _target; }
    }

    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;

    /// <summary>
    /// Publishes the monitor's latest decision. A null target means VRChat is
    /// gone and the capture returns to the system mix. The returned task
    /// completes once this decision was applied or superseded by a newer one.
    /// </summary>
    public async Task ApplyTargetAsync(ProcessIdentity? target, CancellationToken cancellationToken = default)
    {
        // The decision is recorded before the gate so an attempt that is still
        // running for the previous decision can notice that it was superseded
        // and discard its own result.
        var generation = Interlocked.Increment(ref _generation);
        lock (_sync) _target = target;
        CancelActiveAttempt();

        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ObjectDisposedException(nameof(AdaptiveLoopbackAudioCapture));
        }

        try
        {
            ThrowIfDisposed();
            if (generation != Volatile.Read(ref _generation)) return;
            await DecideAsync(target, generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Clears the failure backoff, for example after the user changed the output
    /// device: the new device is a new situation and deserves an immediate try.
    /// A platform that cannot capture process audio at all stays unsupported.
    /// </summary>
    public void ResetBackoff() => ClearBackoff();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ObjectDisposedException(nameof(AdaptiveLoopbackAudioCapture));
        }

        try
        {
            ThrowIfDisposed();
            if (_started) return;

            _started = true;
            _stoppedPublished = false;
            ResumeDispatch();
            _lifetime = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _generation);
            var target = Volatile.Read(ref _target);
            try
            {
                // The system mix is always available first; a known target is
                // chased right after the capture is live.
                await PublishSystemLoopbackAsync(target is not null, generation, cancellationToken).ConfigureAwait(false);
                if (target is not null)
                {
                    await DecideAsync(target, generation, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _started = false;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false)) return;
        try
        {
            await StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!await EnterAsync(CancellationToken.None).ConfigureAwait(false)) return;

        var disposeGate = false;
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopLockedAsync().ConfigureAwait(false);
            disposeGate = true;
        }
        finally
        {
            _gate.Release();
            // Waiting callers observe ObjectDisposedException and give up; the
            // capture itself is already fully released at this point.
            if (disposeGate) _gate.Dispose();
        }

        _lifetime.Dispose();
    }

    private async Task StopLockedAsync()
    {
        if (!_started) return;

        _started = false;
        CancelActiveAttempt();
        var lifetime = _lifetime;
        _lifetime = new CancellationTokenSource();
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        lifetime.Dispose();
        ClearBackoff();
        lock (_sync) _target = null;

        var kind = _state.Kind;
        var previous = _current;
        SuspendDispatch();
        _current = null;
        if (previous is not null) Detach(previous);
        await StopQuietlyAsync(previous).ConfigureAwait(false);
        ResumeDispatch();
        SetState(AudioSourceState.SystemLoopback);

        if (!_stoppedPublished)
        {
            _stoppedPublished = true;
            RaiseStopped(kind);
        }
    }

    private async Task DecideAsync(ProcessIdentity? target, long generation, CancellationToken cancellationToken)
    {
        if (!_started) return;

        if (target is null)
        {
            // VRChat is gone, so the compatibility label goes away with it.
            await PublishSystemLoopbackAsync(compatibilityMode: false, generation, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_state.Kind == AudioCaptureSourceKind.ProcessLoopback &&
            _state.ProcessIdentity is { } bound &&
            bound.Matches(target) &&
            _current is not null)
        {
            return; // Already capturing this exact instance.
        }

        if (_processLoopbackUnavailable || IsBackingOff(target))
        {
            await PublishSystemLoopbackAsync(compatibilityMode: true, generation, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SwitchToProcessAsync(target, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task SwitchToProcessAsync(ProcessIdentity target, long generation, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _activeAttempt, attempt);
        CandidateStart started;
        try
        {
            started = await StartCandidateAsync(AudioCaptureRequest.ProcessLoopback(target), attempt.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _activeAttempt, null);
        }

        if (IsSuperseded(generation))
        {
            // A newer decision already owns the state; this candidate never runs.
            await DisposeQuietlyAsync(started.Capture).ConfigureAwait(false);
            return;
        }

        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

        if (started.Outcome == CandidateOutcome.NotSupported)
        {
            _processLoopbackUnavailable = true;
            ClearBackoff();
            RaiseFaulted(AudioCaptureSourceKind.ProcessLoopback, target, started.Failure!);
            await TryPublishSystemLoopbackAsync(compatibilityMode: true, generation, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (started.Outcome == CandidateOutcome.Failed)
        {
            var firstFailure = RecordFailure(target);
            if (firstFailure) RaiseFaulted(AudioCaptureSourceKind.ProcessLoopback, target, started.Failure!);
            await KeepCurrentOrFallBackAsync(generation, cancellationToken).ConfigureAwait(false);
            ScheduleRetry(target);
            return;
        }

        ClearBackoff();
        await InstallAsync(
            started.Capture!,
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, target),
            generation).ConfigureAwait(false);
    }

    private async Task PublishSystemLoopbackAsync(bool compatibilityMode, long generation, CancellationToken cancellationToken)
    {
        var kind = compatibilityMode
            ? AudioCaptureSourceKind.SystemLoopbackFallback
            : AudioCaptureSourceKind.SystemLoopback;

        if (_current is not null &&
            _state.Kind is AudioCaptureSourceKind.SystemLoopback or AudioCaptureSourceKind.SystemLoopbackFallback)
        {
            // The running capture already delivers the system mix; only the
            // reason shown next to it changes, so this is not a stream boundary.
            PublishIfChanged(new AudioSourceState(kind, null, _state.LastFaultCode), generation, isBoundary: false);
            return;
        }

        var request = compatibilityMode
            ? AudioCaptureRequest.SystemLoopbackFallback()
            : AudioCaptureRequest.SystemLoopback();
        var started = await StartCandidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (started.Capture is null)
        {
            throw started.Failure ?? new InvalidOperationException("无法启动系统回环采集。");
        }

        await InstallAsync(started.Capture, new AudioSourceState(kind, null, _state.LastFaultCode), generation)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryPublishSystemLoopbackAsync(
        bool compatibilityMode,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await PublishSystemLoopbackAsync(compatibilityMode, generation, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RaiseFaulted(
                compatibilityMode ? AudioCaptureSourceKind.SystemLoopbackFallback : AudioCaptureSourceKind.SystemLoopback,
                null,
                exception);
            return false;
        }
    }

    /// <summary>
    /// A failed candidate must not interrupt audio that is already flowing, so
    /// the running capture is kept. Only when there is nothing left to hear does
    /// the coordinator rebuild the system mix and mark the compatibility mode.
    /// </summary>
    private async Task KeepCurrentOrFallBackAsync(long generation, CancellationToken cancellationToken)
    {
        if (_current is not null)
        {
            if (_state.Kind is AudioCaptureSourceKind.SystemLoopback or AudioCaptureSourceKind.SystemLoopbackFallback)
            {
                PublishIfChanged(
                    new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback, null, _state.LastFaultCode),
                    generation,
                    isBoundary: false);
            }

            return;
        }

        await TryPublishSystemLoopbackAsync(compatibilityMode: true, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallAsync(IAudioCapture candidate, AudioSourceState state, long generation)
    {
        // 1. stop feeding the recognizer, 2. announce the boundary while the old
        // stream is still alive, 3. retire the old capture, 4. resume feeding.
        SuspendDispatch();
        var previous = _current;
        if (previous is not null) Detach(previous);
        Attach(candidate);
        _current = candidate;
        SetState(state);
        RaiseSourceChanged(state, generation, isBoundary: true);
        try
        {
            await StopQuietlyAsync(previous).ConfigureAwait(false);
        }
        finally
        {
            ResumeDispatch();
        }
    }

    /// <summary>
    /// Closes the dispatch window. It returns only when every buffer that was
    /// already on its way to the recognizer has been delivered, so audio from
    /// the retired source can never land after the boundary reset.
    /// </summary>
    private void SuspendDispatch()
    {
        lock (_dispatchSync) _dispatching = false;
    }

    private void ResumeDispatch()
    {
        lock (_dispatchSync) _dispatching = true;
    }

    private void PublishIfChanged(AudioSourceState state, long generation, bool isBoundary)
    {
        if (Equals(_state, state)) return;
        SetState(state);
        RaiseSourceChanged(state, generation, isBoundary);
    }

    private async Task<CandidateStart> StartCandidateAsync(AudioCaptureRequest request, CancellationToken cancellationToken)
    {
        IAudioCapture? candidate = null;
        try
        {
            candidate = _factory.Create(request);
            await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
            return new CandidateStart(candidate, CandidateOutcome.Started, null);
        }
        catch (ProcessLoopbackNotSupportedException exception)
        {
            await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
            return new CandidateStart(null, CandidateOutcome.NotSupported, exception);
        }
        catch (Exception exception)
        {
            await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
            return new CandidateStart(null, CandidateOutcome.Failed, exception);
        }
    }

    private void OnSamplesReady(object? sender, AudioSamplesEventArgs args)
    {
        // Samples from a retired capture, or from the swap window, never reach
        // the recognizer: that is what keeps two sources out of one caption.
        lock (_dispatchSync)
        {
            if (!_dispatching || !ReferenceEquals(sender, _current)) return;
            try { SamplesReady?.Invoke(this, args); }
            catch { /* Capture callbacks run on a device thread; subscriber faults must not tear it down. */ }
        }
    }

    private void OnCurrentFaulted(object? sender, AudioCaptureFaultedEventArgs args)
    {
        if (!ReferenceEquals(sender, _current)) return;
        // Recovery is asynchronous so the device callback thread is never blocked.
        _ = HandleSourceFailureAsync(sender, args.SourceKind, args.Exception);
    }

    private void OnCurrentStopped(object? sender, AudioCaptureStoppedEventArgs args)
    {
        if (!ReferenceEquals(sender, _current)) return;
        // A stop the coordinator did not ask for means the stream ended; the
        // same rebuild-then-fall-back rules apply.
        _ = HandleSourceFailureAsync(
            sender,
            args.SourceKind,
            args.Exception ?? new IOException("音频采集已意外停止。"));
    }

    private async Task HandleSourceFailureAsync(object? sender, AudioCaptureSourceKind sourceKind, Exception exception)
    {
        if (!await EnterAsync(CancellationToken.None).ConfigureAwait(false)) return;
        try
        {
            if (_disposed || !_started) return;
            // The capture may already have been replaced while this recovery
            // waited for the gate; the report only concerns the current one.
            if (!ReferenceEquals(sender, _current)) return;
            var generation = Interlocked.Increment(ref _generation);
            CancelActiveAttempt();

            // The broken capture must not be reused, and it cannot serve as the
            // "keep the current source" fallback either.
            var broken = _current;
            SuspendDispatch();
            _current = null;
            if (broken is not null) Detach(broken);
            await StopQuietlyAsync(broken).ConfigureAwait(false);

            var target = Volatile.Read(ref _target);
            var wasProcessLoopback = sourceKind == AudioCaptureSourceKind.ProcessLoopback ||
                                     _state.Kind == AudioCaptureSourceKind.ProcessLoopback;
            if (target is not null && wasProcessLoopback)
            {
                if (await TryRecoverProcessAsync(target, generation).ConfigureAwait(false)) return;

                var firstFailure = RecordFailure(target);
                if (firstFailure) RaiseFaulted(AudioCaptureSourceKind.ProcessLoopback, target, exception);
                await TryPublishSystemLoopbackAsync(compatibilityMode: true, generation, CancellationToken.None)
                    .ConfigureAwait(false);
                ScheduleRetry(target);
                return;
            }

            // Either there is no target, or the system mix itself failed: rebuilt
            // as the same source, and the fault only surfaces when that fails.
            await TryPublishSystemLoopbackAsync(
                compatibilityMode: target is not null,
                generation,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ResumeDispatch();
            _gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the same process loopback source after a device failure. The old
    /// capture is already gone, so a fresh start is the only proof the source works.
    /// </summary>
    private async Task<bool> TryRecoverProcessAsync(ProcessIdentity target, long generation)
    {
        if (_processLoopbackUnavailable || IsBackingOff(target)) return false;

        var started = await StartCandidateAsync(AudioCaptureRequest.ProcessLoopback(target), CancellationToken.None)
            .ConfigureAwait(false);
        if (started.Outcome == CandidateOutcome.NotSupported)
        {
            _processLoopbackUnavailable = true;
            ClearBackoff();
            RaiseFaulted(AudioCaptureSourceKind.ProcessLoopback, target, started.Failure!);
            return false;
        }

        if (started.Outcome != CandidateOutcome.Started) return false;

        ClearBackoff();
        await InstallAsync(
            started.Capture!,
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, target),
            generation).ConfigureAwait(false);
        return true;
    }

    private void ScheduleRetry(ProcessIdentity target) => _ = RunRetryAsync(target, CurrentBackoffDelay());

    private async Task RunRetryAsync(ProcessIdentity target, TimeSpan delay)
    {
        CancellationToken lifetime;
        try { lifetime = _lifetime.Token; }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (delay > TimeSpan.Zero) await _options.DelayAsync(delay, lifetime).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancelled or broken delay ends this retry; Stop, Dispose or a
            // newer decision owns the state from here.
            return;
        }

        if (!await EnterAsync(CancellationToken.None).ConfigureAwait(false)) return;
        try
        {
            ClearBackoffPending(target);
            if (_disposed || !_started) return;
            if (Volatile.Read(ref _target) is not { } current || !current.Matches(target)) return;
            await DecideAsync(target, Volatile.Read(ref _generation), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RaiseFaulted(AudioCaptureSourceKind.ProcessLoopback, target, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts one failure for the identity; true when it is the first of the run.</summary>
    private bool RecordFailure(ProcessIdentity target)
    {
        lock (_sync)
        {
            if (_failedTarget is null || !_failedTarget.Matches(target))
            {
                _failedTarget = target;
                _failureCount = 0;
            }

            _failureCount++;
            _backoffPending = true;
            return _failureCount == 1;
        }
    }

    private void ClearBackoff()
    {
        lock (_sync)
        {
            _failedTarget = null;
            _failureCount = 0;
            _backoffPending = false;
        }
    }

    private void ClearBackoffPending(ProcessIdentity target)
    {
        lock (_sync)
        {
            if (_failedTarget is not null && _failedTarget.Matches(target)) _backoffPending = false;
        }
    }

    private bool IsBackingOff(ProcessIdentity target)
    {
        lock (_sync)
        {
            return _backoffPending && _failedTarget is not null && _failedTarget.Matches(target);
        }
    }

    private TimeSpan CurrentBackoffDelay()
    {
        var schedule = _options.FailureBackoff;
        if (schedule.Count == 0) return TimeSpan.Zero;
        int failureCount;
        lock (_sync) failureCount = _failureCount;
        var index = Math.Min(Math.Max(failureCount, 1) - 1, schedule.Count - 1);
        var delay = schedule[index];
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private void Attach(IAudioCapture capture)
    {
        capture.SamplesReady += OnSamplesReady;
        capture.Faulted += OnCurrentFaulted;
        capture.Stopped += OnCurrentStopped;
    }

    private void Detach(IAudioCapture capture)
    {
        capture.SamplesReady -= OnSamplesReady;
        capture.Faulted -= OnCurrentFaulted;
        capture.Stopped -= OnCurrentStopped;
    }

    private void SetState(AudioSourceState state)
    {
        lock (_sync) _state = state;
    }

    private bool IsSuperseded(long generation) => generation != Volatile.Read(ref _generation);

    private void CancelActiveAttempt()
    {
        var attempt = Volatile.Read(ref _activeAttempt);
        if (attempt is null) return;
        try { attempt.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void RaiseSourceChanged(AudioSourceState state, long generation, bool isBoundary)
    {
        try { SourceChanged?.Invoke(this, new AudioSourceChangedEventArgs(state, generation, isBoundary)); }
        catch { /* Subscriber failures must not tear down capture. */ }
    }

    private void RaiseFaulted(AudioCaptureSourceKind kind, ProcessIdentity? identity, Exception exception)
    {
        try { Faulted?.Invoke(this, new AudioCaptureFaultedEventArgs(kind, exception, faultCode: null, identity)); }
        catch { }
    }

    private void RaiseStopped(AudioCaptureSourceKind kind)
    {
        try { Stopped?.Invoke(this, new AudioCaptureStoppedEventArgs(kind)); }
        catch { }
    }

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IAudioCapture? capture)
    {
        if (capture is null) return;
        try { await capture.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private static async Task StopQuietlyAsync(IAudioCapture? capture)
    {
        if (capture is null) return;
        try { await capture.StopAsync().ConfigureAwait(false); }
        catch { }
        await DisposeQuietlyAsync(capture).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdaptiveLoopbackAudioCapture));
    }

    private enum CandidateOutcome
    {
        Started,
        NotSupported,
        Failed
    }

    private readonly record struct CandidateStart(IAudioCapture? Capture, CandidateOutcome Outcome, Exception? Failure);
}
