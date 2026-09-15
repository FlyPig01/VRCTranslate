using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The cross-machine robustness measures of the process loopback path. One
/// machine where the MTA activation works proves nothing about the next one, so
/// each measure is pinned here with a scripted seam: the activation timeout, the
/// single apartment retry, the format ladder, the session-wide fuse and the
/// diagnostics log.
/// </summary>
public sealed class ProcessLoopbackRobustnessTests
{
    private static readonly ProcessIdentity Target = new(4_242);
    private const int UnsupportedFormat = unchecked((int)0x88890008);

    [Fact]
    public void An_activation_that_never_completes_times_out_instead_of_waiting_forever()
    {
        var activator = new WasapiProcessLoopbackActivator(
            new ScriptedApartmentHost(),
            new ScriptedActivationCall(immediateHResult: 0));
        var options = new ProcessLoopbackActivationOptions
        {
            ActivationTimeout = TimeSpan.FromMilliseconds(150),
            ActivationPollInterval = TimeSpan.FromMilliseconds(15)
        };
        var trace = new ProcessLoopbackActivationTrace("MTA", 26_200);
        var started = Stopwatch.StartNew();

        var exception = Assert.Throws<ProcessLoopbackActivationTimeoutException>(() => activator.Activate(
            Target.ProcessId,
            ProcessLoopbackApartment.Multithreaded,
            options,
            trace,
            CancellationToken.None));
        started.Stop();

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"激活超时必须由注入的超时值决定，实际等待 {started.Elapsed}。");
        Assert.Equal(ProcessLoopbackActivationTimeoutException.ActivationTimeoutCode, ProcessLoopbackFault.CodeOf(exception));
        // The immediate HRESULT is recorded before the wait, which is exactly
        // what makes a support log say "the call was accepted, no callback came".
        Assert.Equal(0, trace.ImmediateHResult);
        Assert.Null(trace.ActivateResult);
    }

    [Fact]
    public async Task A_timed_out_activation_is_a_fault_with_the_timeout_code()
    {
        var diagnostics = new RecordingDiagnostics();
        var sessionFactory = CreateSessionFactory(
            diagnostics,
            new WasapiProcessLoopbackActivator(new ScriptedApartmentHost(), new ScriptedActivationCall(0)),
            new ScriptedClientFactory(new ScriptedProcessLoopbackClient(rejections: 0)),
            new ProcessLoopbackActivationOptions
            {
                ActivationTimeout = TimeSpan.FromMilliseconds(120),
                ActivationPollInterval = TimeSpan.FromMilliseconds(15)
            });
        var faults = new List<AudioCaptureFaultedEventArgs>();
        await using var capture = new ProcessLoopbackAudioCapture(Target, sessionFactory);
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };

        var thrown = await Assert.ThrowsAsync<ProcessLoopbackActivationTimeoutException>(() => capture.StartAsync());

        Assert.Equal(ProcessLoopbackActivationTimeoutException.ActivationTimeoutCode, ProcessLoopbackFault.CodeOf(thrown));
        Assert.Equal("ActivationTimeout", Assert.Single(faults).FaultCode);
        // A timeout is transient: the machine may simply be busy, so the
        // coordinator's backoff owns the retry instead of the fuse.
        Assert.Equal(ProcessLoopbackFaultKind.Transient, ProcessLoopbackFault.Classify(thrown));
        Assert.Equal("ActivationTimeout", Assert.Single(diagnostics.Reports).FaultCode);
    }

    [Fact]
    public async Task An_apartment_failure_is_retried_exactly_once_in_the_other_apartment()
    {
        var refused = new COMException("该单元不允许激活", ProcessLoopbackFault.WrongThread);
        var apartmentHost = new ScriptedApartmentHost();
        var breaker = new ProcessLoopbackCircuitBreaker();
        var session = new ScriptedProcessLoopbackSession(ScriptedAudio.FloatFormat, []);
        var sessionFactory = new ScriptedProcessLoopbackSessionFactory((_, apartment, _) =>
            apartment == ProcessLoopbackApartment.Multithreaded ? throw refused : session);
        await using var capture = new ProcessLoopbackAudioCapture(
            Target,
            new ProcessLoopbackCaptureDependencies(sessionFactory, apartmentHost, breaker));

        await capture.StartAsync();
        await capture.StopAsync();

        Assert.Equal(2, sessionFactory.OpenCount);
        Assert.Equal(
            [ProcessLoopbackApartment.Multithreaded, ProcessLoopbackApartment.SingleThreaded],
            sessionFactory.Apartments);
        Assert.Equal(
            [ProcessLoopbackApartment.Multithreaded, ProcessLoopbackApartment.SingleThreaded],
            apartmentHost.Entered);
        Assert.Equal(2, apartmentHost.LeaveCount);
        // The retry succeeded, so nothing is broken and nothing is fused.
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public async Task A_second_apartment_failure_is_permanent_and_never_retried_again()
    {
        // The retry is the last chance whatever it answers with: a device error
        // on the retry must not turn into "retry again in 30 seconds" on a
        // machine whose COM environment already refused the activation.
        var refused = new COMException("该单元不允许激活", ProcessLoopbackFault.WrongThread);
        var invalidated = new COMException("设备已失效", unchecked((int)0x88890026));
        var apartmentHost = new ScriptedApartmentHost();
        var breaker = new ProcessLoopbackCircuitBreaker();
        var sessionFactory = new ScriptedProcessLoopbackSessionFactory((_, apartment, _) =>
            apartment == ProcessLoopbackApartment.Multithreaded ? throw refused : throw invalidated);
        var faults = new List<AudioCaptureFaultedEventArgs>();
        await using var capture = new ProcessLoopbackAudioCapture(
            Target,
            new ProcessLoopbackCaptureDependencies(sessionFactory, apartmentHost, breaker));
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };

        var thrown = await Assert.ThrowsAsync<ProcessLoopbackNotSupportedException>(() => capture.StartAsync());

        Assert.Equal(2, sessionFactory.OpenCount);
        Assert.Equal(
            [ProcessLoopbackApartment.Multithreaded, ProcessLoopbackApartment.SingleThreaded],
            apartmentHost.Entered);
        Assert.True(breaker.IsOpen);
        // The retry's own HRESULT is what the failure reports.
        Assert.Equal(unchecked((int)0x88890026), thrown.HResult);
        Assert.Contains("0x88890026", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("NotSupported", Assert.Single(faults).FaultCode);
    }

    [Fact]
    public async Task A_permanent_hresult_is_unsupported_without_any_apartment_retry()
    {
        var notImplemented = new COMException("该设备不支持", ProcessLoopbackFault.NotImplemented);
        var apartmentHost = new ScriptedApartmentHost();
        var breaker = new ProcessLoopbackCircuitBreaker();
        var sessionFactory = new ScriptedProcessLoopbackSessionFactory((_, _, _) => throw notImplemented);
        await using var capture = new ProcessLoopbackAudioCapture(
            Target,
            new ProcessLoopbackCaptureDependencies(sessionFactory, apartmentHost, breaker));

        var thrown = await Assert.ThrowsAsync<ProcessLoopbackNotSupportedException>(() => capture.StartAsync());

        Assert.Equal(1, sessionFactory.OpenCount);
        Assert.Equal([ProcessLoopbackApartment.Multithreaded], apartmentHost.Entered);
        Assert.Equal(ProcessLoopbackFault.NotImplemented, thrown.HResult);
        Assert.True(breaker.IsOpen);
        Assert.Equal("0x80004001", breaker.Reason);
    }

    [Fact]
    public async Task A_permanent_failure_trips_the_factory_fuse_so_the_next_start_touches_no_com()
    {
        var breaker = new ProcessLoopbackCircuitBreaker();
        var diagnostics = new RecordingDiagnostics();
        var factory = new WindowsAudioCaptureFactory(
            new StubProcessLoopbackSupport(isSupported: true, build: 26_200),
            ProcessLoopbackActivationOptions.Default,
            diagnostics,
            breaker);
        var permanent = new COMException("类未注册", ProcessLoopbackFault.ClassNotRegistered);
        var sessionFactory = new ScriptedProcessLoopbackSessionFactory((_, _, _) => throw permanent);

        await using (var capture = new ProcessLoopbackAudioCapture(
            Target,
            new ProcessLoopbackCaptureDependencies(sessionFactory, new ScriptedApartmentHost(), breaker)))
        {
            await Assert.ThrowsAsync<ProcessLoopbackNotSupportedException>(() => capture.StartAsync());
        }

        Assert.True(breaker.IsOpen);
        var exception = Assert.Throws<ProcessLoopbackNotSupportedException>(
            () => factory.Create(AudioCaptureRequest.ProcessLoopback(Target)));
        Assert.Contains("0x80040154", exception.Message, StringComparison.Ordinal);

        // The fuse only concerns process loopback: the system mix keeps working.
        await using var loopback = factory.Create(AudioCaptureRequest.SystemLoopback());
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, loopback.SourceKind);
    }

    [Fact]
    public void The_format_ladder_moves_on_until_the_client_accepts_one()
    {
        var diagnostics = new RecordingDiagnostics();
        var endpoint = new WaveFormatExtensible(48_000, 32, 2);
        var client = new ScriptedProcessLoopbackClient(rejections: 3, rejectionHResult: UnsupportedFormat);
        var sessionFactory = CreateSessionFactory(
            diagnostics,
            new ScriptedActivator(new object()),
            new ScriptedClientFactory(client),
            new ProcessLoopbackActivationOptions
            {
                FormatCandidates =
                [
                    new WaveFormatExtensible(48_000, 32, 2),
                    new WaveFormatExtensible(48_000, 16, 2),
                    new WaveFormatExtensible(44_100, 16, 2),
                    new WaveFormatExtensible(16_000, 16, 1)
                ]
            },
            new StubRenderFormatProvider(endpoint));

        using var session = sessionFactory.Open(Target, ProcessLoopbackApartment.Multithreaded, CancellationToken.None);

        // The endpoint format is the first candidate and the ladder is walked in
        // order; the client accepted the fourth one.
        Assert.Equal(4, client.InitializeCount);
        Assert.Equal(
            ["48000 Hz/32-bit float/2ch", "48000 Hz/16-bit pcm/2ch", "44100 Hz/16-bit pcm/2ch"],
            client.Rejected);
        // The session decodes with the accepted format, not with the first one.
        Assert.Equal(16_000, session.Format.SampleRate);
        Assert.Equal(1, session.Format.Channels);

        var report = Assert.Single(diagnostics.Reports);
        Assert.Equal("16000 Hz/16-bit pcm/1ch", report.AcceptedFormat);
        Assert.Equal(4, report.FormatAttempts.Count);
        Assert.Equal(UnsupportedFormat, report.FormatAttempts[0].HResult);
        Assert.Equal(0, report.FormatAttempts[3].HResult);
        Assert.Equal("48000 Hz/32-bit float/2ch", report.DefaultEndpointFormat);
        Assert.Equal("MTA", report.Apartment);
        Assert.Equal(26_200, report.Build);
        Assert.Null(report.FaultCode);
    }

    [Fact]
    public void A_client_that_refuses_every_format_reports_the_whole_ladder()
    {
        var diagnostics = new RecordingDiagnostics();
        var client = new ScriptedProcessLoopbackClient(rejections: int.MaxValue, rejectionHResult: UnsupportedFormat);
        var sessionFactory = CreateSessionFactory(
            diagnostics,
            new ScriptedActivator(new object()),
            new ScriptedClientFactory(client),
            new ProcessLoopbackActivationOptions
            {
                FormatCandidates = [new WaveFormatExtensible(48_000, 16, 2), new WaveFormatExtensible(16_000, 16, 1)]
            },
            new StubRenderFormatProvider(new WaveFormatExtensible(48_000, 32, 2)));

        var exception = Assert.Throws<COMException>(
            () => sessionFactory.Open(Target, ProcessLoopbackApartment.Multithreaded, CancellationToken.None));

        Assert.Equal(UnsupportedFormat, exception.HResult);
        Assert.Equal(3, client.InitializeCount);
        var report = Assert.Single(diagnostics.Reports);
        Assert.Null(report.AcceptedFormat);
        Assert.Equal(3, report.FormatAttempts.Count);
        Assert.All(report.FormatAttempts, attempt => Assert.Equal(UnsupportedFormat, attempt.HResult));
        Assert.Contains("0x88890008", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ladder_that_runs_into_a_permanent_hresult_is_reported_as_permanent()
    {
        // A client that answers E_NOTIMPL for every format can never be made to
        // work, so the reported HRESULT is the permanent one and the capture
        // fuses instead of walking the ladder again on every retry.
        var diagnostics = new RecordingDiagnostics();
        var client = new ScriptedProcessLoopbackClient(rejections: int.MaxValue, rejectionHResult: ProcessLoopbackFault.NotImplemented);
        var sessionFactory = CreateSessionFactory(
            diagnostics,
            new ScriptedActivator(new object()),
            new ScriptedClientFactory(client),
            new ProcessLoopbackActivationOptions { FormatCandidates = [new WaveFormatExtensible(48_000, 16, 2)] },
            new StubRenderFormatProvider(new WaveFormatExtensible(48_000, 32, 2)));

        var exception = Assert.Throws<COMException>(
            () => sessionFactory.Open(Target, ProcessLoopbackApartment.Multithreaded, CancellationToken.None));

        Assert.Equal(ProcessLoopbackFault.NotImplemented, exception.HResult);
        Assert.Equal(ProcessLoopbackFaultKind.Permanent, ProcessLoopbackFault.Classify(exception));
    }

    [Fact]
    public void The_timeout_and_the_apartment_rules_are_the_documented_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProcessLoopbackActivationOptions.Default.ActivationTimeout);
        Assert.Equal(4, ProcessLoopbackActivationOptions.Default.Candidates.Count);
        Assert.Equal(
            new[] { (48_000, 32, 2), (48_000, 16, 2), (44_100, 16, 2), (16_000, 16, 1) },
            ProcessLoopbackActivationOptions.Default.Candidates
                .Select(candidate => (candidate.SampleRate, candidate.BitsPerSample, candidate.Channels)));
        Assert.Equal(ProcessLoopbackApartment.Multithreaded, ComApartmentHost.Instance.Preferred);
        Assert.Equal(
            ProcessLoopbackApartment.SingleThreaded,
            ComApartmentHost.Instance.AlternateTo(ProcessLoopbackApartment.Multithreaded));
        Assert.Equal(ProcessLoopbackActivationTimeoutException.ActivationTimeoutCode, ProcessLoopbackFault.CodeOf(
            new ProcessLoopbackActivationTimeoutException("超时")));
        Assert.Equal(ProcessLoopbackFaultKind.ApartmentEnvironment, ProcessLoopbackFault.Classify(
            new COMException("单元", ProcessLoopbackFault.AccessDenied)));
        Assert.Equal(ProcessLoopbackFaultKind.Permanent, ProcessLoopbackFault.Classify(
            new COMException("类未注册", ProcessLoopbackFault.ClassNotRegistered)));
        Assert.Equal(ProcessLoopbackFaultKind.Permanent, ProcessLoopbackFault.Classify(
            new ProcessLoopbackNotSupportedException("不支持")));
    }

    private static ProcessLoopbackSessionFactory CreateSessionFactory(
        RecordingDiagnostics diagnostics,
        IProcessLoopbackActivator activator,
        IProcessLoopbackClientFactory clients,
        ProcessLoopbackActivationOptions options,
        IDefaultRenderFormatProvider? renderFormats = null) =>
        new(new ProcessLoopbackSessionFactoryDependencies(
            new StubProcessLoopbackSupport(isSupported: true, build: 26_200),
            activator,
            clients,
            renderFormats ?? new StubRenderFormatProvider(new WaveFormatExtensible(48_000, 32, 2)),
            options,
            diagnostics));
}

/// <summary>Captures the reports instead of writing them to the portable folder.</summary>
internal sealed class RecordingDiagnostics : IProcessLoopbackDiagnostics
{
    private readonly List<ProcessLoopbackDiagnosticsReport> _reports = [];

    public IReadOnlyList<ProcessLoopbackDiagnosticsReport> Reports
    {
        get { lock (_reports) return [.. _reports]; }
    }

    public void Report(ProcessLoopbackDiagnosticsReport report)
    {
        lock (_reports) _reports.Add(report);
    }
}

/// <summary>The immediate activation call, scripted: never completes on request.</summary>
internal sealed class ScriptedActivationCall : IProcessLoopbackActivationCall
{
    private readonly int _immediateHResult;

    public ScriptedActivationCall(int immediateHResult) => _immediateHResult = immediateHResult;

    public int Invoke(
        IntPtr activationParams,
        ProcessLoopbackActivationHandler handler,
        out IActivateAudioInterfaceAsyncOperation? operation)
    {
        operation = null;
        // No completion callback is ever delivered: this is exactly the machine
        // where the activation hangs, which the timeout has to survive.
        return _immediateHResult;
    }
}

/// <summary>An activation that returns a stand-in client object.</summary>
internal sealed class ScriptedActivator : IProcessLoopbackActivator
{
    private readonly object _activated;
    private readonly Exception? _failure;

    public ScriptedActivator(object activated) => _activated = activated;

    public ScriptedActivator(Exception failure)
    {
        _activated = new object();
        _failure = failure;
    }

    public ProcessLoopbackApartment? LastApartment { get; private set; }

    public object Activate(
        int processId,
        ProcessLoopbackApartment apartment,
        ProcessLoopbackActivationOptions options,
        ProcessLoopbackActivationTrace trace,
        CancellationToken cancellationToken)
    {
        LastApartment = apartment;
        trace.ImmediateHResult = 0;
        trace.ActivateResult = 0;
        if (_failure is not null) throw _failure;
        return _activated;
    }
}

/// <summary>Wraps every activated object into one scripted client.</summary>
internal sealed class ScriptedClientFactory : IProcessLoopbackClientFactory
{
    private readonly IProcessLoopbackClient _client;

    public ScriptedClientFactory(IProcessLoopbackClient client) => _client = client;

    public IProcessLoopbackClient Wrap(object activatedInterface) => _client;
}

/// <summary>
/// A client that rejects a scripted number of formats before accepting one, so
/// the order of the candidate ladder is observable.
/// </summary>
internal sealed class ScriptedProcessLoopbackClient : IProcessLoopbackClient
{
    private readonly int _rejections;
    private readonly int _rejectionHResult;
    private readonly List<string> _rejected = [];

    public ScriptedProcessLoopbackClient(int rejections, int rejectionHResult = 0)
    {
        _rejections = rejections;
        _rejectionHResult = rejectionHResult == 0 ? UnsupportedFormatValue : rejectionHResult;
    }

    private static int UnsupportedFormatValue => unchecked((int)0x88890008);

    public int InitializeCount { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Formats that were refused, in order.</summary>
    public IReadOnlyList<string> Rejected => _rejected;

    public void Initialize(WaveFormat format)
    {
        InitializeCount++;
        if (InitializeCount > _rejections) return;
        _rejected.Add(ProcessLoopbackActivationTrace.DescribeFormat(format));
        throw new COMException("格式不受支持", _rejectionHResult);
    }

    public IProcessLoopbackSession CreateSession(WaveFormat format) =>
        new ScriptedProcessLoopbackSession(format, []);

    public void Dispose() => Disposed = true;
}

/// <summary>Returns one fixed render mix format, or none.</summary>
internal sealed class StubRenderFormatProvider : IDefaultRenderFormatProvider
{
    private readonly WaveFormat? _format;
    private readonly string? _failure;

    public StubRenderFormatProvider(WaveFormat? format, string? failure = null)
    {
        _format = format;
        _failure = failure;
    }

    public WaveFormat? TryRead(out string? failure)
    {
        failure = _format is null ? _failure ?? "没有默认播放端点" : null;
        return _format;
    }
}
