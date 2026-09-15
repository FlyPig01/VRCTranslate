using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>Answers the capability question with a scripted build number.</summary>
internal sealed class StubProcessLoopbackSupport : IProcessLoopbackSupport
{
    public StubProcessLoopbackSupport(bool isSupported, int? build)
    {
        IsSupported = isSupported;
        Build = build;
    }

    public bool IsSupported { get; }

    public int? Build { get; }
}

/// <summary>
/// One scripted packet: a buffer, an empty packet, or an error to raise.
/// <paramref name="AnnouncedFrames"/> scripts the size query separately, which is
/// how the real AUDCLNT_S_BUFFER_EMPTY race is reproduced.
/// </summary>
internal readonly record struct ScriptedStep(
    IntPtr Data,
    int FrameCount,
    AudioClientBufferFlags Flags,
    Exception? Fault,
    int? AnnouncedFrames = null);

/// <summary>
/// Scripted stand-in for an activated process loopback client. It lets the
/// capture loop policy - silence, empty packets, gaps, HRESULT faults and
/// stop-once - be proven without an audio device.
/// </summary>
internal sealed class ScriptedProcessLoopbackSession : IProcessLoopbackSession
{
    private readonly Queue<ScriptedStep> _steps;
    private readonly List<IntPtr> _allocations = [];
    private readonly List<int> _releasedFrames = [];
    private readonly ManualResetEventSlim _idle = new(false);

    public ScriptedProcessLoopbackSession(WaveFormat format, IEnumerable<ScriptedStep> steps, int bufferSize = 480)
    {
        Format = format;
        BufferSize = bufferSize;
        _steps = new Queue<ScriptedStep>(steps);
        foreach (var step in _steps)
        {
            if (step.Data != IntPtr.Zero) _allocations.Add(step.Data);
        }
    }

    public WaveFormat Format { get; }

    public int BufferSize { get; }

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Frame counts the capture loop handed back, in order.</summary>
    public IReadOnlyList<int> ReleasedFrames => _releasedFrames;

    public int PendingFrames
    {
        get
        {
            if (_steps.Count == 0) return 0;
            var step = _steps.Peek();
            return step.AnnouncedFrames ?? step.FrameCount;
        }
    }

    public void Start() => Started = true;

    public void Stop() => Stopped = true;

    public ProcessLoopbackPacket Acquire()
    {
        var step = _steps.Dequeue();
        if (step.Fault is not null) throw step.Fault;
        return new ProcessLoopbackPacket(step.Data, step.FrameCount, step.Flags);
    }

    public void Release(int frameCount) => _releasedFrames.Add(frameCount);

    public bool WaitForPacket(TimeSpan timeout)
    {
        if (_steps.Count > 0) return true;
        // Nothing scripted any more: behave like an idle device and let the
        // caller's timeout and cancellation checks run.
        _idle.Wait(timeout);
        return _steps.Count > 0;
    }

    public void Dispose()
    {
        Disposed = true;
        foreach (var pointer in _allocations) Marshal.FreeHGlobal(pointer);
        _allocations.Clear();
        _idle.Dispose();
    }
}

/// <summary>Hands out scripted sessions, or fails the way a real activation would.</summary>
internal sealed class ScriptedProcessLoopbackSessionFactory : IProcessLoopbackSessionFactory
{
    private readonly Func<ProcessIdentity, ProcessLoopbackApartment, CancellationToken, IProcessLoopbackSession> _open;
    private readonly Queue<IProcessLoopbackSession>? _sessions;
    private readonly List<ProcessLoopbackApartment> _apartments = [];

    public ScriptedProcessLoopbackSessionFactory(
        Func<ProcessIdentity, ProcessLoopbackApartment, CancellationToken, IProcessLoopbackSession> open) =>
        _open = open;

    public ScriptedProcessLoopbackSessionFactory(params IProcessLoopbackSession[] sessions)
    {
        _sessions = new Queue<IProcessLoopbackSession>(sessions);
        _open = (_, _, _) => _sessions.Count > 0
            ? _sessions.Dequeue()
            : throw new InvalidOperationException("没有更多脚本化采集会话。");
    }

    public int OpenCount { get; private set; }

    public ProcessIdentity? LastTarget { get; private set; }

    /// <summary>Apartments the opens ran in, in order; the retry policy is pinned with this.</summary>
    public IReadOnlyList<ProcessLoopbackApartment> Apartments
    {
        get { lock (_apartments) return [.. _apartments]; }
    }

    public IProcessLoopbackSession Open(
        ProcessIdentity target,
        ProcessLoopbackApartment apartment,
        CancellationToken cancellationToken)
    {
        OpenCount++;
        LastTarget = target;
        lock (_apartments) _apartments.Add(apartment);
        return _open(target, apartment, cancellationToken);
    }
}

/// <summary>
/// An apartment host that records the apartments it was asked for and can refuse
/// the ones a test wants refused, which is how the one-retry rule is proven
/// without a machine whose COM environment actually misbehaves.
/// </summary>
internal sealed class ScriptedApartmentHost : IProcessLoopbackApartmentHost
{
    private readonly Func<ProcessLoopbackApartment, Exception?>? _enterFailure;
    private readonly List<ProcessLoopbackApartment> _entered = [];

    public ScriptedApartmentHost(ProcessLoopbackApartment preferred = ProcessLoopbackApartment.Multithreaded)
        : this(preferred, enterFailure: null)
    {
    }

    public ScriptedApartmentHost(
        ProcessLoopbackApartment preferred,
        Func<ProcessLoopbackApartment, Exception?>? enterFailure)
    {
        Preferred = preferred;
        _enterFailure = enterFailure;
    }

    public ProcessLoopbackApartment Preferred { get; }

    /// <summary>Apartments Enter was called for, in order.</summary>
    public IReadOnlyList<ProcessLoopbackApartment> Entered
    {
        get { lock (_entered) return [.. _entered]; }
    }

    public int LeaveCount { get; private set; }

    public ProcessLoopbackApartment AlternateTo(ProcessLoopbackApartment apartment) =>
        apartment == ProcessLoopbackApartment.Multithreaded
            ? ProcessLoopbackApartment.SingleThreaded
            : ProcessLoopbackApartment.Multithreaded;

    public string Describe(ProcessLoopbackApartment apartment) =>
        ProcessLoopbackApartmentName.Describe(apartment);

    public void Enter(ProcessLoopbackApartment apartment)
    {
        lock (_entered) _entered.Add(apartment);
        if (_enterFailure?.Invoke(apartment) is { } failure) throw failure;
    }

    public void Leave() => LeaveCount++;

    public bool WaitForCompletion(ProcessLoopbackApartment apartment, WaitHandle completion, TimeSpan slice) =>
        completion.WaitOne(slice);
}

/// <summary>Unmanaged test audio in the format WASAPI would deliver.</summary>
internal static class ScriptedAudio
{
    public const int SampleRate = 48_000;
    public const int FramesPerPacket = 480;

    /// <summary>The shared mix format a process loopback stream is asked for.</summary>
    public static WaveFormat FloatFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    /// <summary>Allocates stereo 32-bit float frames of one constant amplitude.</summary>
    public static IntPtr Constant(float amplitude, int frames = FramesPerPacket)
    {
        var bytes = frames * FloatFormat.BlockAlign;
        var pointer = Marshal.AllocHGlobal(bytes);
        var buffer = new byte[bytes];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < FloatFormat.Channels; channel++)
            {
                BitConverter.GetBytes(amplitude).CopyTo(buffer, (frame * FloatFormat.Channels + channel) * 4);
            }
        }

        Marshal.Copy(buffer, 0, pointer, bytes);
        return pointer;
    }
}
