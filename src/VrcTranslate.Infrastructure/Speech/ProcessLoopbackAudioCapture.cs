using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>One packet acquired from a process loopback client.</summary>
/// <remarks>
/// <see cref="Data"/> points into the audio engine's buffer and is only valid
/// until the packet is released, which is why the capture loop copies it on the
/// thread that acquired it.
/// </remarks>
internal readonly record struct ProcessLoopbackPacket(IntPtr Data, int FrameCount, AudioClientBufferFlags Flags);

/// <summary>
/// The slice of an activated process loopback client the capture loop uses.
/// Keeping it behind an interface lets the loop policy - silence, gaps, empty
/// packets, HRESULT faults and stop-once - be proven without an audio device.
/// </summary>
internal interface IProcessLoopbackSession : IDisposable
{
    /// <summary>Format of the packets, already normalized for the sample converter.</summary>
    WaveFormat Format { get; }

    /// <summary>Buffer size in frames, used to size the copy buffer.</summary>
    int BufferSize { get; }

    void Start();

    void Stop();

    /// <summary>Frames in the next packet, or zero when none is ready.</summary>
    int PendingFrames { get; }

    /// <summary>Acquires the next packet; pair with <see cref="Release"/> on the same thread.</summary>
    ProcessLoopbackPacket Acquire();

    /// <summary>Returns one acquired packet to the audio engine.</summary>
    void Release(int frameCount);

    /// <summary>Waits for audio; false when nothing arrived within the timeout.</summary>
    bool WaitForPacket(TimeSpan timeout);
}

/// <summary>
/// Opens one activated process loopback client for a target process. The
/// apartment is part of the call because the wait for the completion callback
/// has to keep an STA served, and because a retry runs in the other apartment.
/// </summary>
internal interface IProcessLoopbackSessionFactory
{
    IProcessLoopbackSession Open(
        ProcessIdentity target,
        ProcessLoopbackApartment apartment,
        CancellationToken cancellationToken);
}

/// <summary>The seams of one process loopback capture.</summary>
/// <remarks>
/// All three are injected so the robustness behaviour the brief asks for - the
/// one apartment retry, the session-wide fuse - is testable on a machine whose
/// COM environment simply works.
/// </remarks>
internal sealed record ProcessLoopbackCaptureDependencies(
    IProcessLoopbackSessionFactory SessionFactory,
    IProcessLoopbackApartmentHost Apartments,
    IProcessLoopbackCircuitBreaker Breaker)
{
    /// <summary>Real dependencies for the default constructor.</summary>
    public static ProcessLoopbackCaptureDependencies CreateDefault() => new(
        new ProcessLoopbackSessionFactory(),
        ComApartmentHost.Instance,
        new ProcessLoopbackCircuitBreaker());
}

/// <summary>
/// Captures only the audio rendered by one process tree, using the Windows
/// Application Loopback interface:
/// <c>ActivateAudioInterfaceAsync</c> →
/// <c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c> →
/// <c>AUDIOCLIENT_ACTIVATION_PARAMS</c> →
/// <c>PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE</c> → target process id.
/// </summary>
/// <remarks>
/// Everything happens on one dedicated thread per attempt: COM is entered
/// there, the activation is started and awaited there, the completion callback
/// arrives on an MTA worker thread, and the capture loop pairs every
/// <c>GetBuffer</c> with its <c>ReleaseBuffer</c> on that same thread. The
/// public start call therefore never blocks a UI thread and never waits on COM.
/// If the COM environment refuses the preferred apartment, exactly one retry
/// runs on a fresh thread in the other apartment; a second failure marks process
/// loopback unsupported for the session instead of retrying forever. Samples
/// leave the capture as mono 16 kHz floats through <see cref="AudioSampleConverter"/>;
/// no NAudio or COM type crosses the application boundary.
/// </remarks>
public sealed class ProcessLoopbackAudioCapture : IAudioCapture
{
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessIdentity _target;
    private readonly ProcessLoopbackCaptureDependencies _dependencies;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Thread? _pump;
    private bool _started;
    private bool _disposed;
    private bool _stopPublished;
    private volatile bool _activated;

    public ProcessLoopbackAudioCapture(ProcessIdentity target)
        : this(target, ProcessLoopbackCaptureDependencies.CreateDefault())
    {
    }

    internal ProcessLoopbackAudioCapture(ProcessIdentity target, IProcessLoopbackSessionFactory sessionFactory)
        : this(
            target,
            new ProcessLoopbackCaptureDependencies(
                sessionFactory,
                ComApartmentHost.Instance,
                new ProcessLoopbackCircuitBreaker()))
    {
    }

    internal ProcessLoopbackAudioCapture(ProcessIdentity target, ProcessLoopbackCaptureDependencies dependencies)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    /// <summary>Legacy mode of the capture boundary; <see cref="SourceKind"/> is the precise value.</summary>
    public AudioCaptureMode Mode => AudioCaptureMode.SystemLoopback;

    public AudioCaptureSourceKind SourceKind => AudioCaptureSourceKind.ProcessLoopback;

    /// <summary>The process tree this capture follows.</summary>
    public ProcessIdentity Target => _target;

    public int SampleRate => AudioSampleConverter.TargetSampleRate;

    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    /// <summary>This capture never changes source, so the event only reports the initial one.</summary>
    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<Exception?> activation;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started) return;

            _started = true;
            _stopPublished = false;
            _activated = false;
            var lifetime = new CancellationTokenSource();
            var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pump = new Thread(() => Pump(lifetime, completion))
            {
                IsBackground = true,
                Name = "VRCTranslate process loopback"
            };
            _lifetime = lifetime;
            _pump = pump;
            pump.Start();
            activation = completion.Task;
        }

        Exception? failure;
        try
        {
            // The wait happens on the pump thread; this continuation only
            // observes its result, so no calling thread ever blocks on COM.
            failure = await activation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (failure is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw failure;
        }

        RaiseSourceChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Thread? pump;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            pump = _pump;
            _pump = null;
            lifetime = _lifetime;
            _lifetime = null;
        }

        if (lifetime is not null)
        {
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        var joined = true;
        if (pump is not null && pump != Thread.CurrentThread)
        {
            joined = await Task.Run(() => pump.Join(StopJoinTimeout)).ConfigureAwait(false);
        }

        if (joined) lifetime?.Dispose();

        // The pump publishes this itself when it reached its own end; the guard
        // inside makes the duplicate call a no-op, and this one still covers a
        // pump that refused to stop within the join timeout.
        if (_activated) PublishStopped(exception: null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the capture on this thread and turns every exit path into exactly one
    /// published outcome. The whole body is guarded: an unexpected exception on a
    /// capture thread would end the process, so even a defect here has to become
    /// a fault event and a fallback to system audio.
    /// </summary>
    private void Pump(CancellationTokenSource lifetime, TaskCompletionSource<Exception?> completion)
    {
        var token = lifetime.Token;
        try
        {
            var preferred = _dependencies.Apartments.Preferred;
            var outcome = RunAttempt(preferred, retried: false, token, completion);
            if (outcome.Kind == ProcessLoopbackAttemptKind.RetryInOtherApartment)
            {
                outcome = RunAttemptOnAlternateThread(preferred, token, completion);
            }

            Publish(outcome, token, completion);
        }
        catch (Exception exception)
        {
            // Last line of defence: report, never throw out of the thread.
            completion.TrySetResult(exception);
            RaiseFaulted(exception);
        }
    }

    private void Publish(
        AttemptOutcome outcome,
        CancellationToken token,
        TaskCompletionSource<Exception?> completion)
    {
        switch (outcome.Kind)
        {
            case ProcessLoopbackAttemptKind.Stopped:
                completion.TrySetResult(null);
                PublishStopped(exception: null);
                break;
            case ProcessLoopbackAttemptKind.Cancelled:
                completion.TrySetResult(new OperationCanceledException(token));
                break;
            case ProcessLoopbackAttemptKind.StartFailed:
                completion.TrySetResult(outcome.Failure!);
                RaiseFaulted(outcome.Failure!);
                break;
            case ProcessLoopbackAttemptKind.Faulted:
                completion.TrySetResult(null);
                RaiseFaulted(outcome.Failure!);
                PublishStopped(outcome.Failure);
                break;
            default:
                completion.TrySetResult(new InvalidOperationException($"未处理的采集结果：{outcome.Kind}。"));
                break;
        }
    }

    /// <summary>
    /// One attempt in one apartment: enter COM, activate, start, drain, release -
    /// all on the calling thread, which is the thread the capture was created for.
    /// </summary>
    private AttemptOutcome RunAttempt(
        ProcessLoopbackApartment apartment,
        bool retried,
        CancellationToken token,
        TaskCompletionSource<Exception?> completion)
    {
        var apartments = _dependencies.Apartments;
        IProcessLoopbackSession? session = null;
        var entered = false;
        var activated = false;
        try
        {
            apartments.Enter(apartment);
            entered = true;
            session = _dependencies.SessionFactory.Open(_target, apartment, token);
            session.Start();
            activated = true;
            _activated = true;
            completion.TrySetResult(null);
            Drain(session, token);
            return AttemptOutcome.Stopped;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A stop the caller asked for is not a fault.
            return AttemptOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            if (activated) return AttemptOutcome.Faulted(exception);
            if (!retried && ProcessLoopbackFault.Classify(exception) == ProcessLoopbackFaultKind.ApartmentEnvironment)
            {
                return AttemptOutcome.RetryInOtherApartment;
            }

            return AttemptOutcome.StartFailed(MakeStartFailure(exception, retried));
        }
        finally
        {
            if (session is not null)
            {
                try { session.Stop(); } catch (Exception) { }
                try { session.Dispose(); } catch (Exception) { }
            }

            if (entered)
            {
                try { apartments.Leave(); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// The one allowed retry: a fresh thread in the other apartment. An apartment
    /// is a property of a thread, so this cannot be done on the failed one.
    /// The retry thread is joined here, which keeps the "no thread outlives the
    /// capture" rule: StopAsync joins this thread and therefore the retry too.
    /// </summary>
    private AttemptOutcome RunAttemptOnAlternateThread(
        ProcessLoopbackApartment preferred,
        CancellationToken token,
        TaskCompletionSource<Exception?> completion)
    {
        var apartment = _dependencies.Apartments.AlternateTo(preferred);
        AttemptOutcome? outcome = null;
        var thread = new Thread(() => outcome = RunAttempt(apartment, retried: true, token, completion))
        {
            IsBackground = true,
            Name = $"VRCTranslate process loopback ({_dependencies.Apartments.Describe(apartment)})"
        };
        thread.Start();
        thread.Join();
        return outcome ?? AttemptOutcome.Cancelled;
    }

    /// <summary>
    /// The failure the caller and the coordinator see. A failure that cannot
    /// succeed on a later attempt becomes <see cref="ProcessLoopbackNotSupportedException"/>
    /// - the signal that makes the coordinator stop trying for the rest of the
    /// session - and opens the fuse so a later session start answers without
    /// touching COM.
    /// </summary>
    private Exception MakeStartFailure(Exception exception, bool afterApartmentRetry)
    {
        // "The retry failed too" is enough: the one allowed second chance is the
        // last one, whatever the second attempt answered with. Retrying a machine
        // whose COM environment already refused the activation is what produces
        // the 3-second-interval nagging the fuse exists to prevent.
        var kind = ProcessLoopbackFault.Classify(exception);
        var permanent = kind == ProcessLoopbackFaultKind.Permanent || afterApartmentRetry;
        if (!permanent) return exception;

        _dependencies.Breaker.Trip(ProcessLoopbackFault.CodeOf(exception) ?? exception.GetType().Name);
        return exception is COMException com
            ? new ProcessLoopbackNotSupportedException(
                $"本机进程回环不可用（{ProcessLoopbackFault.Describe(com.HResult)}，HRESULT 0x{com.HResult:X8}），"
                + "本会话不再尝试，继续使用系统回环。",
                com.HResult)
            : new ProcessLoopbackNotSupportedException(
                $"本机进程回环不可用（{exception.Message}），本会话不再尝试，继续使用系统回环。",
                exception);
    }

    /// <summary>
    /// Event driven drain loop. Every acquired packet is released on this same
    /// thread, silent packets never become recognizer input. An empty packet -
    /// <c>AUDCLNT_S_BUFFER_EMPTY</c>, a success code - carries no buffer at all
    /// and must not be released; skipping that rule once exhausts the buffer and
    /// the stream goes silent without any error.
    /// </summary>
    private void Drain(IProcessLoopbackSession session, CancellationToken token)
    {
        var format = session.Format;
        var buffer = Array.Empty<byte>();
        while (!token.IsCancellationRequested)
        {
            if (!session.WaitForPacket(WaitSlice)) continue;

            while (!token.IsCancellationRequested)
            {
                if (session.PendingFrames <= 0) break;

                var packet = session.Acquire();
                try
                {
                    if (packet.FrameCount <= 0) break;
                    if ((packet.Flags & AudioClientBufferFlags.Silent) != 0) continue;
                    Deliver(packet, format, ref buffer);
                }
                finally
                {
                    if (packet.FrameCount > 0) session.Release(packet.FrameCount);
                }
            }
        }
    }

    private void Deliver(ProcessLoopbackPacket packet, WaveFormat format, ref byte[] buffer)
    {
        var required = packet.FrameCount * format.BlockAlign;
        if (required <= 0) return;
        if (buffer.Length < required) buffer = new byte[required];

        Marshal.Copy(packet.Data, buffer, 0, required);
        var samples = AudioSampleConverter.ToMono16k(buffer, required, format);
        if (samples.Length == 0) return;
        try
        {
            SamplesReady?.Invoke(this, new AudioSamplesEventArgs(samples, SampleRate));
        }
        catch (Exception)
        {
            // Subscriber faults must never tear down the capture loop.
        }
    }

    private void RaiseSourceChanged()
    {
        try
        {
            SourceChanged?.Invoke(this, new AudioSourceChangedEventArgs(
                new AudioSourceState(SourceKind, _target), generation: 0, isBoundary: false));
        }
        catch (Exception) { }
    }

    private void RaiseFaulted(Exception exception)
    {
        try
        {
            Faulted?.Invoke(this, new AudioCaptureFaultedEventArgs(
                SourceKind,
                exception,
                ProcessLoopbackFault.CodeOf(exception),
                _target));
        }
        catch (Exception) { }
    }

    private void PublishStopped(Exception? exception)
    {
        lock (_sync)
        {
            if (_stopPublished) return;
            _stopPublished = true;
        }

        try { Stopped?.Invoke(this, new AudioCaptureStoppedEventArgs(SourceKind, exception)); }
        catch (Exception) { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLoopbackAudioCapture));
    }

    private enum ProcessLoopbackAttemptKind
    {
        Stopped,
        Cancelled,
        StartFailed,
        Faulted,
        RetryInOtherApartment
    }

    private readonly record struct AttemptOutcome(ProcessLoopbackAttemptKind Kind, Exception? Failure)
    {
        public static AttemptOutcome Stopped { get; } = new(ProcessLoopbackAttemptKind.Stopped, null);

        public static AttemptOutcome Cancelled { get; } = new(ProcessLoopbackAttemptKind.Cancelled, null);

        public static AttemptOutcome RetryInOtherApartment { get; } =
            new(ProcessLoopbackAttemptKind.RetryInOtherApartment, null);

        public static AttemptOutcome StartFailed(Exception failure) =>
            new(ProcessLoopbackAttemptKind.StartFailed, failure);

        public static AttemptOutcome Faulted(Exception failure) =>
            new(ProcessLoopbackAttemptKind.Faulted, failure);
    }
}

/// <summary>
/// Initializes COM on a capture thread. Windows calls the activation completion
/// handler from a worker thread in the multi-threaded apartment, so the thread
/// that starts the activation has to live in that same apartment; the capture
/// owns dedicated threads and never borrows the UI thread. The single-threaded
/// apartment is supported only as the one retry for machines that refuse the
/// MTA, and then the wait has to keep its message queue served.
/// </summary>
internal static class ComApartment
{
    private const uint CoInitMultithreaded = 0x0;
    private const uint CoInitApartmentThreaded = 0x2;

    // COWAIT_DISPATCH_CALLS | COWAIT_DISPATCH_WINDOW_MESSAGES
    private const uint CoWaitDispatchCalls = 0x8;
    private const uint CoWaitDispatchWindowMessages = 0x10;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoWaitForMultipleHandles(
        uint flags,
        uint timeout,
        uint handleCount,
        IntPtr[] handles,
        out uint index);

    /// <summary>Enters the requested apartment; throws when the thread refuses it.</summary>
    public static void Enter(ProcessLoopbackApartment apartment)
    {
        var hresult = CoInitializeEx(
            IntPtr.Zero,
            apartment == ProcessLoopbackApartment.Multithreaded ? CoInitMultithreaded : CoInitApartmentThreaded);
        if (hresult == ProcessLoopbackFault.ChangedMode)
        {
            throw new COMException($"进程回环采集线程不能运行在现有的 COM 单元中（{apartment}）。", hresult);
        }

        // S_OK and S_FALSE are both success: the thread is in the apartment and
        // the matching CoUninitialize is required.
        Marshal.ThrowExceptionForHR(hresult);
    }

    /// <summary>Leaves the apartment entered by <see cref="Enter"/>.</summary>
    public static void Leave()
    {
        try { CoUninitialize(); }
        catch (Exception) { }
    }

    /// <summary>
    /// Waits for one completion slice in an apartment-compatible way. In the MTA
    /// the event is simply waited on; in an STA the COM callback is delivered
    /// through the message queue, so blocking outright would never see it.
    /// </summary>
    public static bool WaitForCompletion(ProcessLoopbackApartment apartment, WaitHandle completion, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (apartment == ProcessLoopbackApartment.Multithreaded) return completion.WaitOne(timeout);

        var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        var handles = new[] { completion.SafeWaitHandle.DangerousGetHandle() };
        var hresult = CoWaitForMultipleHandles(
            CoWaitDispatchCalls | CoWaitDispatchWindowMessages,
            milliseconds,
            1,
            handles,
            out _);
        return hresult >= 0;
    }
}
