using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
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

/// <summary>Opens one activated process loopback client for a target process.</summary>
internal interface IProcessLoopbackSessionFactory
{
    IProcessLoopbackSession Open(ProcessIdentity target, CancellationToken cancellationToken);
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
/// Everything happens on one dedicated MTA thread: COM is entered there, the
/// activation is started and awaited there, the completion callback arrives on
/// an MTA worker thread, and the capture loop pairs every <c>GetBuffer</c> with
/// its <c>ReleaseBuffer</c> on that same thread. The public start call therefore
/// never blocks a UI thread and never waits on COM. Samples leave the capture as
/// mono 16 kHz floats through <see cref="AudioSampleConverter"/>; no NAudio or
/// COM type crosses the application boundary.
/// </remarks>
public sealed class ProcessLoopbackAudioCapture : IAudioCapture
{
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessIdentity _target;
    private readonly IProcessLoopbackSessionFactory _sessionFactory;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Thread? _pump;
    private bool _started;
    private bool _disposed;
    private bool _stopPublished;
    private volatile bool _activated;

    public ProcessLoopbackAudioCapture(ProcessIdentity target)
        : this(target, new ProcessLoopbackSessionFactory())
    {
    }

    internal ProcessLoopbackAudioCapture(ProcessIdentity target, IProcessLoopbackSessionFactory sessionFactory)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
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

    private void Pump(CancellationTokenSource lifetime, TaskCompletionSource<Exception?> completion)
    {
        var token = lifetime.Token;
        IProcessLoopbackSession? session = null;
        Exception? fault = null;
        Exception? startFailure = null;
        var activated = false;
        var comEntered = false;
        try
        {
            ComApartment.EnterMultithreaded();
            comEntered = true;
            session = _sessionFactory.Open(_target, token);
            session.Start();
            activated = true;
            _activated = true;
            completion.TrySetResult(null);
            Drain(session, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A stop the caller asked for is not a fault.
        }
        catch (Exception exception)
        {
            if (activated)
            {
                fault = exception;
            }
            else
            {
                // The start call observes this through the activation task; the
                // fault event reports the HRESULT to anyone already listening.
                startFailure = exception;
                completion.TrySetResult(exception);
            }
        }
        finally
        {
            if (session is not null)
            {
                try { session.Stop(); } catch (Exception) { }
                try { session.Dispose(); } catch (Exception) { }
            }

            if (comEntered) ComApartment.Leave();

            // A start that never produced a session reports its failure through
            // the activation task and the fault event, never through Stopped.
            completion.TrySetResult(new OperationCanceledException(token));
            var reported = fault ?? startFailure;
            if (reported is not null) RaiseFaulted(reported);
            if (activated) PublishStopped(fault);
        }
    }

    /// <summary>
    /// Event driven drain loop. Every acquired packet is released on this same
    /// thread, silent packets never become recognizer input, and a discontinuity
    /// packet is still delivered because it carries real audio with a gap.
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
                    // AUDCLNT_S_BUFFER_EMPTY can win the race against the size
                    // query; such a packet carries no buffer and must not be
                    // released. A silent packet is not audio and is dropped.
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
                exception is COMException com ? ProcessLoopbackFault.Describe(com.HResult) : null,
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
}

/// <summary>
/// Activates and owns one Windows process loopback client. This is the only
/// place that builds the activation PROPVARIANT and drives the asynchronous
/// activation, and it runs on the capture's own MTA thread.
/// </summary>
internal sealed class ProcessLoopbackSessionFactory : IProcessLoopbackSessionFactory
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Requested buffer duration in 100 ns units: 20 ms, as in the Windows sample.</summary>
    private const long BufferDurationHns = 200_000;

    public IProcessLoopbackSession Open(ProcessIdentity target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var handler = new ProcessLoopbackActivationHandler();
        var activationParams = new ProcessLoopbackActivationParams(target.ProcessId);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The immediate HRESULT and the asynchronous result are both
            // checked: a failure here means no callback will ever arrive.
            var immediate = ProcessLoopbackInterop.Activate(activationParams.Pointer, handler, out operation);
            if (immediate < 0)
            {
                throw ProcessLoopbackFault.Create("进程回环激活调用失败", immediate);
            }

            var waited = TimeSpan.Zero;
            while (!handler.Wait(ActivationPollInterval))
            {
                waited += ActivationPollInterval;
                if (cancellationToken.IsCancellationRequested)
                {
                    // Windows may still be reading the blob, so ownership goes
                    // to the callback instead of being freed here.
                    handler.TakeOwnership(activationParams.Detach());
                    throw new OperationCanceledException(cancellationToken);
                }

                if (waited >= ActivationTimeout)
                {
                    handler.TakeOwnership(activationParams.Detach());
                    throw new TimeoutException(
                        $"进程回环激活在 {ActivationTimeout.TotalSeconds:0} 秒内没有完成。");
                }
            }

            if (handler.Failure is not null) throw handler.Failure;
            if (handler.ActivateResult < 0)
            {
                throw ProcessLoopbackFault.Create("进程回环激活失败", handler.ActivateResult);
            }

            var activated = handler.ActivatedInterface
                ?? throw new InvalidOperationException("进程回环激活没有返回音频客户端。");
            return CreateSession(activated);
        }
        finally
        {
            activationParams.Dispose();
            // Releasing the operation is what lets Windows drop its reference to
            // the completion handler. The whole class only ever runs on Windows,
            // but the project targets a platform-neutral framework.
            if (operation is not null && OperatingSystem.IsWindows()) Marshal.ReleaseComObject(operation);
            handler.Close();
        }
    }

    private static IProcessLoopbackSession CreateSession(object activatedInterface)
    {
        if (activatedInterface is not IAudioClient audioClientInterface)
        {
            throw new InvalidCastException("进程回环激活结果没有提供 IAudioClient 接口。");
        }

        // NAudio wraps the activated client instead of declaring IAudioClient,
        // IAudioCaptureClient and AUDCLNT_* again.
        var audioClient = new AudioClient(audioClientInterface);
        try
        {
            // A process loopback client is not backed by a device, so
            // GetMixFormat - like GetDevicePeriod and GetStreamLatency - answers
            // E_NOTIMPL (0x80004001) instead of a format. The stream is therefore
            // initialized with the current default render endpoint's mix format,
            // which is what the captured process audio is mixed for; the engine
            // converts whatever the target actually renders. The raw value goes
            // back to Initialize unchanged, and only the sample converter sees
            // the normalized sample type (WAVE_FORMAT_EXTENSIBLE carries the real
            // type in its sub-format).
            var mixFormat = ReadDefaultRenderMixFormat();
            var captureFormat = AudioSampleConverter.NormalizeFormat(mixFormat);
            if (!AudioSampleConverter.IsConvertible(captureFormat))
            {
                throw new NotSupportedException($"进程回环需要使用不支持的音频格式：{captureFormat}。");
            }

            var dataEvent = new AutoResetEvent(false);
            try
            {
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                    BufferDurationHns,
                    0,
                    mixFormat,
                    Guid.Empty);
                audioClient.SetEventHandle(dataEvent.SafeWaitHandle.DangerousGetHandle());
                return new WasapiProcessLoopbackSession(audioClient, captureFormat, audioClient.BufferSize, dataEvent);
            }
            catch
            {
                dataEvent.Dispose();
                throw;
            }
        }
        catch
        {
            audioClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Mix format of the current default render endpoint. A process loopback
    /// stream has no format of its own, so this is the format it is asked for.
    /// </summary>
    private static WaveFormat ReadDefaultRenderMixFormat()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = device.AudioClient;
            return client.MixFormat;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "系统没有可用的默认播放端点，无法确定进程回环的音频格式。", exception);
        }
    }

    /// <summary>
    /// The activated WASAPI client. Stop and release happen in the documented
    /// order - stop the stream, release the client, then close the event handle.
    /// </summary>
    private sealed class WasapiProcessLoopbackSession : IProcessLoopbackSession
    {
        private readonly AudioClient _audioClient;
        private readonly AudioCaptureClient _captureClient;
        private readonly AutoResetEvent _dataEvent;
        private bool _started;
        private bool _disposed;

        public WasapiProcessLoopbackSession(
            AudioClient audioClient,
            WaveFormat format,
            int bufferSize,
            AutoResetEvent dataEvent)
        {
            _audioClient = audioClient;
            _dataEvent = dataEvent;
            Format = format;
            BufferSize = Math.Max(1, bufferSize);
            _captureClient = audioClient.AudioCaptureClient;
        }

        public WaveFormat Format { get; }

        public int BufferSize { get; }

        public int PendingFrames => _captureClient.GetNextPacketSize();

        public void Start()
        {
            ThrowIfDisposed();
            if (_started) return;
            _audioClient.Start();
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _audioClient.Stop();
        }

        public ProcessLoopbackPacket Acquire()
        {
            var data = _captureClient.GetBuffer(out var frames, out var flags, out _, out _);
            return new ProcessLoopbackPacket(data, frames, flags);
        }

        public void Release(int frameCount)
        {
            if (frameCount > 0) _captureClient.ReleaseBuffer(frameCount);
        }

        public bool WaitForPacket(TimeSpan timeout) => _dataEvent.WaitOne(timeout);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch (Exception) { }
            _audioClient.Dispose();
            _dataEvent.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WasapiProcessLoopbackSession));
        }
    }
}

/// <summary>
/// Initializes COM on the capture thread. Windows calls the activation
/// completion handler from a worker thread in the multi-threaded apartment, so
/// the thread that starts the activation has to live in that same apartment;
/// the capture owns a dedicated thread and never borrows the UI thread.
/// </summary>
internal static class ComApartment
{
    private const uint CoInitMultithreaded = 0x0;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    /// <summary>Enters the MTA; throws when the thread is already in an STA.</summary>
    public static void EnterMultithreaded()
    {
        var hresult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hresult == RpcChangedMode)
        {
            throw new COMException("进程回环采集线程不能运行在 STA 单元中。", hresult);
        }

        // S_OK and S_FALSE are both success: the thread is in the MTA and the
        // matching CoUninitialize is required.
        Marshal.ThrowExceptionForHR(hresult);
    }

    /// <summary>Leaves the apartment entered by <see cref="EnterMultithreaded"/>.</summary>
    public static void Leave()
    {
        try { CoUninitialize(); }
        catch (Exception) { }
    }
}
