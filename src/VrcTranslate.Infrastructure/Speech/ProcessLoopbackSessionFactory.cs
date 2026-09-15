using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Runs the asynchronous activation of one process loopback client.</summary>
/// <remarks>
/// Split from the factory so the two things the brief demands can be proven
/// without audio hardware: that an activation which never completes times out
/// instead of waiting forever, and that a client which refuses the first formats
/// still ends up initialized.
/// </remarks>
internal interface IProcessLoopbackActivator
{
    /// <summary>
    /// Starts the activation and waits for its completion, filling the immediate
    /// and asynchronous HRESULTs into <paramref name="trace"/>. Returns the
    /// activated COM object; throws the failure otherwise.
    /// </summary>
    object Activate(
        int processId,
        ProcessLoopbackApartment apartment,
        ProcessLoopbackActivationOptions options,
        ProcessLoopbackActivationTrace trace,
        CancellationToken cancellationToken);
}

/// <summary>The immediate <c>ActivateAudioInterfaceAsync</c> call.</summary>
internal interface IProcessLoopbackActivationCall
{
    int Invoke(
        IntPtr activationParams,
        ProcessLoopbackActivationHandler handler,
        out IActivateAudioInterfaceAsyncOperation? operation);
}

/// <summary>Reads the mix format a device-less process loopback client is asked for first.</summary>
internal interface IDefaultRenderFormatProvider
{
    /// <summary>The default render endpoint's mix format, or null with the reason it failed.</summary>
    WaveFormat? TryRead(out string? failure);
}

/// <summary>Wraps the activated COM object into the client the format negotiation uses.</summary>
internal interface IProcessLoopbackClientFactory
{
    IProcessLoopbackClient Wrap(object activatedInterface);
}

/// <summary>One activated client whose <c>Initialize</c> is tried with candidate formats.</summary>
internal interface IProcessLoopbackClient : IDisposable
{
    /// <summary>Initializes the stream with one candidate; throws the HRESULT it answers with.</summary>
    void Initialize(WaveFormat format);

    /// <summary>A startable session for the accepted format; ownership of the client moves to it.</summary>
    IProcessLoopbackSession CreateSession(WaveFormat format);
}

/// <summary>The seams of one session factory.</summary>
internal sealed record ProcessLoopbackSessionFactoryDependencies(
    IProcessLoopbackSupport Support,
    IProcessLoopbackActivator Activator,
    IProcessLoopbackClientFactory Clients,
    IDefaultRenderFormatProvider RenderFormats,
    ProcessLoopbackActivationOptions Options,
    IProcessLoopbackDiagnostics Diagnostics)
{
    /// <summary>Real dependencies for the default constructor.</summary>
    public static ProcessLoopbackSessionFactoryDependencies CreateDefault() => new(
        WindowsProcessLoopbackSupport.Instance,
        new WasapiProcessLoopbackActivator(ComApartmentHost.Instance, new WasapiProcessLoopbackActivationCall()),
        WasapiProcessLoopbackClientFactory.Instance,
        WasapiDefaultRenderFormatProvider.Instance,
        ProcessLoopbackActivationOptions.Default,
        FileProcessLoopbackDiagnostics.Shared);
}

/// <summary>
/// Activates and owns one Windows process loopback client. This is the only
/// place that builds the activation PROPVARIANT, drives the asynchronous
/// activation and negotiates the stream format, and it runs on the capture's own
/// attempt thread. Every attempt writes one diagnostics report - the successful
/// ones say which format was accepted, the failed ones say which HRESULT came
/// back from where.
/// </summary>
internal sealed class ProcessLoopbackSessionFactory : IProcessLoopbackSessionFactory
{
    private readonly ProcessLoopbackSessionFactoryDependencies _dependencies;

    public ProcessLoopbackSessionFactory()
        : this(ProcessLoopbackSessionFactoryDependencies.CreateDefault())
    {
    }

    internal ProcessLoopbackSessionFactory(ProcessLoopbackSessionFactoryDependencies dependencies) =>
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

    public IProcessLoopbackSession Open(
        ProcessIdentity target,
        ProcessLoopbackApartment apartment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var trace = new ProcessLoopbackActivationTrace(
            ProcessLoopbackApartmentName.Describe(apartment),
            _dependencies.Support.Build);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activated = _dependencies.Activator.Activate(
                target.ProcessId,
                apartment,
                _dependencies.Options,
                trace,
                cancellationToken);
            return NegotiateFormat(activated, trace);
        }
        catch (Exception exception)
        {
            trace.FaultCode = ProcessLoopbackFault.CodeOf(exception) ?? exception.GetType().Name;
            trace.Message = exception.Message;
            throw;
        }
        finally
        {
            // Diagnostics are best effort by contract: a read-only data folder
            // must cost the log line, never the audio capture.
            try { _dependencies.Diagnostics.Report(trace.ToReport()); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Asks the activated client for the default endpoint's mix format first and
    /// then walks the candidate ladder until one is accepted. A process loopback
    /// client is not backed by a device, so it has no format of its own; the
    /// engine converts whatever the target actually renders into whatever was
    /// accepted here, and only the accepted format reaches the sample converter.
    /// </summary>
    private IProcessLoopbackSession NegotiateFormat(object activatedInterface, ProcessLoopbackActivationTrace trace)
    {
        var client = _dependencies.Clients.Wrap(activatedInterface);
        try
        {
            WaveFormat? accepted = null;
            foreach (var candidate in BuildCandidates(trace))
            {
                if (!AudioSampleConverter.IsConvertible(candidate))
                {
                    trace.RecordAttempt(candidate, ProcessLoopbackFault.NotSupportedByPlatform, "无法转换为 16 kHz 单声道");
                    continue;
                }

                try
                {
                    client.Initialize(candidate);
                    trace.RecordAttempt(candidate, 0, failure: null);
                    accepted = candidate;
                    break;
                }
                catch (COMException exception)
                {
                    trace.RecordAttempt(candidate, exception.HResult, failure: null);
                }
                catch (Exception exception)
                {
                    // A managed refusal still counts as a rejected candidate; its
                    // type is more informative than the generic HRESULT it carries.
                    trace.RecordAttempt(candidate, exception.HResult, exception.GetType().Name);
                }
            }

            if (accepted is null) throw CreateFormatFault(trace);
            trace.AcceptedFormat = ProcessLoopbackActivationTrace.DescribeFormat(accepted);
            return client.CreateSession(AudioSampleConverter.NormalizeFormat(accepted));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private IReadOnlyList<WaveFormat> BuildCandidates(ProcessLoopbackActivationTrace trace)
    {
        var candidates = new List<WaveFormat>();
        var mixFormat = _dependencies.RenderFormats.TryRead(out var failure);
        if (mixFormat is not null)
        {
            trace.DefaultEndpointFormat = ProcessLoopbackActivationTrace.DescribeFormat(mixFormat);
            candidates.Add(mixFormat);
        }
        else
        {
            trace.DefaultEndpointFailure = failure;
        }

        foreach (var candidate in _dependencies.Options.Candidates)
        {
            if (!candidates.Any(existing => IsSameFormat(existing, candidate))) candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    /// No candidate was accepted. The HRESULT reported is a permanent one when
    /// any candidate produced one, because that is the value that lets the
    /// capture stop asking instead of retrying a client that will refuse every
    /// format again.
    /// </summary>
    private static COMException CreateFormatFault(ProcessLoopbackActivationTrace trace)
    {
        var permanent = trace.Attempts.FirstOrDefault(
            attempt => ProcessLoopbackFault.ClassifyHResult(attempt.HResult) == ProcessLoopbackFaultKind.Permanent);
        var reported = permanent.Format is not null
            ? permanent.HResult
            : trace.Attempts.Count > 0
                ? trace.Attempts[^1].HResult
                : ProcessLoopbackFault.NotSupportedByPlatform;
        var tried = string.Join("、", trace.Attempts.Select(attempt => $"{attempt.Format}=0x{attempt.HResult:X8}"));
        var endpoint = trace.DefaultEndpointFailure is { } failure ? $"；默认播放端点不可用：{failure}" : string.Empty;
        return ProcessLoopbackFault.Create($"进程回环不接受任何候选音频格式（{tried}{endpoint}）", reported);
    }

    private static bool IsSameFormat(WaveFormat left, WaveFormat right)
    {
        var first = AudioSampleConverter.NormalizeFormat(left);
        var second = AudioSampleConverter.NormalizeFormat(right);
        return first.SampleRate == second.SampleRate
            && first.BitsPerSample == second.BitsPerSample
            && first.Channels == second.Channels
            && first.Encoding == second.Encoding;
    }
}

/// <summary>The real <c>ActivateAudioInterfaceAsync</c> call.</summary>
internal sealed class WasapiProcessLoopbackActivationCall : IProcessLoopbackActivationCall
{
    public int Invoke(
        IntPtr activationParams,
        ProcessLoopbackActivationHandler handler,
        out IActivateAudioInterfaceAsyncOperation? operation) =>
        ProcessLoopbackInterop.Activate(activationParams, handler, out operation);
}

/// <summary>
/// Drives one asynchronous activation: start it, wait for the completion
/// callback inside the attempt's apartment, and surface both HRESULTs. The wait
/// is bounded by <see cref="ProcessLoopbackActivationOptions.ActivationTimeout"/>,
/// so a machine where the callback never arrives costs five seconds once, not a
/// capture that hangs forever.
/// </summary>
internal sealed class WasapiProcessLoopbackActivator : IProcessLoopbackActivator
{
    private readonly IProcessLoopbackApartmentHost _apartments;
    private readonly IProcessLoopbackActivationCall _call;

    public WasapiProcessLoopbackActivator(IProcessLoopbackApartmentHost apartments, IProcessLoopbackActivationCall call)
    {
        _apartments = apartments ?? throw new ArgumentNullException(nameof(apartments));
        _call = call ?? throw new ArgumentNullException(nameof(call));
    }

    public object Activate(
        int processId,
        ProcessLoopbackApartment apartment,
        ProcessLoopbackActivationOptions options,
        ProcessLoopbackActivationTrace trace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);
        var handler = new ProcessLoopbackActivationHandler();
        var activationParams = new ProcessLoopbackActivationParams(processId);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The immediate HRESULT and the asynchronous result are both
            // checked: a failure here means no callback will ever arrive.
            var immediate = _call.Invoke(activationParams.Pointer, handler, out operation);
            trace.ImmediateHResult = immediate;
            if (immediate < 0)
            {
                throw ProcessLoopbackFault.Create("进程回环激活调用失败", immediate);
            }

            var waited = TimeSpan.Zero;
            while (!_apartments.WaitForCompletion(apartment, handler.CompletionHandle, options.ActivationPollInterval))
            {
                waited += options.ActivationPollInterval;
                if (cancellationToken.IsCancellationRequested)
                {
                    // Windows may still be reading the blob, so ownership goes
                    // to the callback instead of being freed here.
                    handler.TakeOwnership(activationParams.Detach());
                    throw new OperationCanceledException(cancellationToken);
                }

                if (waited >= options.ActivationTimeout)
                {
                    handler.TakeOwnership(activationParams.Detach());
                    throw new ProcessLoopbackActivationTimeoutException(
                        $"进程回环激活在 {options.ActivationTimeout.TotalSeconds:0.#} 秒内没有完成。");
                }
            }

            if (handler.Failure is not null) throw handler.Failure;
            trace.ActivateResult = handler.ActivateResult;
            if (handler.ActivateResult < 0)
            {
                throw ProcessLoopbackFault.Create("进程回环激活失败", handler.ActivateResult);
            }

            return handler.ActivatedInterface
                ?? throw new InvalidOperationException("进程回环激活没有返回音频客户端。");
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
}

/// <summary>The real default render endpoint mix format.</summary>
internal sealed class WasapiDefaultRenderFormatProvider : IDefaultRenderFormatProvider
{
    /// <summary>Stateless; shared by every factory.</summary>
    public static WasapiDefaultRenderFormatProvider Instance { get; } = new();

    public WaveFormat? TryRead(out string? failure)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = device.AudioClient;
            failure = null;
            return client.MixFormat;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return null;
        }
    }
}

/// <summary>Turns the activated COM interface into the NAudio client wrapper.</summary>
internal sealed class WasapiProcessLoopbackClientFactory : IProcessLoopbackClientFactory
{
    /// <summary>Stateless; shared by every factory.</summary>
    public static WasapiProcessLoopbackClientFactory Instance { get; } = new();

    public IProcessLoopbackClient Wrap(object activatedInterface)
    {
        if (activatedInterface is not IAudioClient audioClientInterface)
        {
            throw new InvalidCastException("进程回环激活结果没有提供 IAudioClient 接口。");
        }

        // NAudio wraps the activated client instead of declaring IAudioClient,
        // IAudioCaptureClient and AUDCLNT_* again.
        return new WasapiProcessLoopbackClient(new AudioClient(audioClientInterface));
    }
}

/// <summary>
/// One activated client. <c>Initialize</c> is called once per candidate until
/// one is accepted; the NAudio client is released here when no candidate was, or
/// handed to the session that owns it afterwards.
/// </summary>
internal sealed class WasapiProcessLoopbackClient : IProcessLoopbackClient
{
    /// <summary>Requested buffer duration in 100 ns units: 20 ms, as in the Windows sample.</summary>
    private const long BufferDurationHns = 200_000;

    private readonly AudioClient _audioClient;
    private bool _initialized;
    private bool _disposed;

    public WasapiProcessLoopbackClient(AudioClient audioClient) =>
        _audioClient = audioClient ?? throw new ArgumentNullException(nameof(audioClient));

    public void Initialize(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        // The candidate goes back to Initialize unchanged: WAVE_FORMAT_EXTENSIBLE
        // carries the real sample type in its sub-format, and only the sample
        // converter sees the normalized type.
        _audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            BufferDurationHns,
            0,
            format,
            Guid.Empty);
        _initialized = true;
    }

    public IProcessLoopbackSession CreateSession(WaveFormat format)
    {
        if (!_initialized) throw new InvalidOperationException("进程回环客户端尚未初始化。");
        var dataEvent = new AutoResetEvent(false);
        try
        {
            _audioClient.SetEventHandle(dataEvent.SafeWaitHandle.DangerousGetHandle());
            return new WasapiProcessLoopbackSession(_audioClient, format, _audioClient.BufferSize, dataEvent);
        }
        catch
        {
            dataEvent.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audioClient.Dispose();
    }
}

/// <summary>
/// The activated WASAPI client. Stop and release happen in the documented order -
/// stop the stream, release the client, then close the event handle.
/// </summary>
internal sealed class WasapiProcessLoopbackSession : IProcessLoopbackSession
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

    /// <summary>Frames announced by the size query; zero is the empty-packet race.</summary>
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
