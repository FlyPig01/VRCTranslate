using NAudio.Wave;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// A COM apartment a process loopback activation can run in. The Windows
/// Application Loopback sample uses the MTA, so that is the preferred one; the
/// single-threaded apartment exists only as the one allowed retry for machines
/// whose COM environment rejects the MTA.
/// </summary>
internal enum ProcessLoopbackApartment
{
    /// <summary><c>COINIT_MULTITHREADED</c>; the apartment the Windows sample uses.</summary>
    Multithreaded,

    /// <summary><c>COINIT_APARTMENTTHREADED</c>; the retry apartment.</summary>
    SingleThreaded
}

/// <summary>Short apartment names ("MTA"/"STA") for the diagnostics log.</summary>
internal static class ProcessLoopbackApartmentName
{
    public static string Describe(ProcessLoopbackApartment apartment) =>
        apartment == ProcessLoopbackApartment.Multithreaded ? "MTA" : "STA";
}

/// <summary>
/// The apartment seams of one capture attempt: entering and leaving the
/// apartment on the calling thread, choosing the retry apartment, waiting for a
/// completion event in an apartment-compatible way, and naming the apartment for
/// the diagnostics log.
/// </summary>
/// <remarks>
/// Injected because the machine running the tests accepts the MTA: the
/// "environment refuses this apartment, retry in the other one" path can only be
/// proven with a substitute implementation.
/// </remarks>
internal interface IProcessLoopbackApartmentHost
{
    /// <summary>The apartment a first attempt uses.</summary>
    ProcessLoopbackApartment Preferred { get; }

    /// <summary>The one apartment a failed preferred attempt is retried in.</summary>
    ProcessLoopbackApartment AlternateTo(ProcessLoopbackApartment apartment);

    /// <summary>Short name of the apartment for the diagnostics log ("MTA"/"STA").</summary>
    string Describe(ProcessLoopbackApartment apartment);

    /// <summary>Enters the apartment on the calling thread; throws the HRESULT when refused.</summary>
    void Enter(ProcessLoopbackApartment apartment);

    /// <summary>Leaves the apartment entered by <see cref="Enter"/>.</summary>
    void Leave();

    /// <summary>
    /// Waits up to <paramref name="slice"/> for <paramref name="completion"/>.
    /// An STA receives COM callbacks through its message queue, so the default
    /// implementation keeps that queue served instead of blocking outright.
    /// </summary>
    bool WaitForCompletion(ProcessLoopbackApartment apartment, WaitHandle completion, TimeSpan slice);
}

/// <summary>The real apartment handling on top of <see cref="ComApartment"/>.</summary>
internal sealed class ComApartmentHost : IProcessLoopbackApartmentHost
{
    /// <summary>Stateless; shared by every capture.</summary>
    public static ComApartmentHost Instance { get; } = new();

    public ProcessLoopbackApartment Preferred => ProcessLoopbackApartment.Multithreaded;

    public ProcessLoopbackApartment AlternateTo(ProcessLoopbackApartment apartment) =>
        apartment == ProcessLoopbackApartment.Multithreaded
            ? ProcessLoopbackApartment.SingleThreaded
            : ProcessLoopbackApartment.Multithreaded;

    public string Describe(ProcessLoopbackApartment apartment) =>
        ProcessLoopbackApartmentName.Describe(apartment);

    public void Enter(ProcessLoopbackApartment apartment) => ComApartment.Enter(apartment);

    public void Leave() => ComApartment.Leave();

    public bool WaitForCompletion(ProcessLoopbackApartment apartment, WaitHandle completion, TimeSpan slice) =>
        ComApartment.WaitForCompletion(apartment, completion, slice);
}

/// <summary>
/// Session-scoped memory of a process loopback failure that retrying cannot fix.
/// It is the one state that turns "this machine cannot capture a single process"
/// into "stop asking": without it every scan of every later start would spend
/// the activation timeout again and keep telling the user about a device that
/// will never answer. System loopback is not affected.
/// </summary>
/// <remarks>
/// Injectable so a test can prove that a tripped fuse stops the factory before
/// it touches COM, and that a permanent HRESULT trips it.
/// </remarks>
public interface IProcessLoopbackCircuitBreaker
{
    /// <summary>True once a permanent failure was recorded; no further attempt is made.</summary>
    bool IsOpen { get; }

    /// <summary>Why the fuse opened, for the diagnostics log; null while closed.</summary>
    string? Reason { get; }

    /// <summary>Records a permanent failure. The first reason wins; false when already open.</summary>
    bool Trip(string reason);

    /// <summary>Closes the fuse again, for example when the output device changed.</summary>
    void Reset();
}

/// <summary>Thread-safe default implementation of <see cref="IProcessLoopbackCircuitBreaker"/>.</summary>
public sealed class ProcessLoopbackCircuitBreaker : IProcessLoopbackCircuitBreaker
{
    private readonly object _sync = new();
    private string? _reason;

    public bool IsOpen
    {
        get { lock (_sync) return _reason is not null; }
    }

    public string? Reason
    {
        get { lock (_sync) return _reason; }
    }

    public bool Trip(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            if (_reason is not null) return false;
            _reason = reason;
            return true;
        }
    }

    public void Reset()
    {
        lock (_sync) _reason = null;
    }
}

/// <summary>
/// The asynchronous activation did not report a result in time. It is a fault
/// with a stable code instead of an exception type the shell would have to know,
/// and it is never treated as "this machine cannot do process loopback": a busy
/// or slow machine deserves the coordinator's backoff retry.
/// </summary>
public sealed class ProcessLoopbackActivationTimeoutException : TimeoutException
{
    /// <summary>Fault code reported through <c>AudioCaptureFaultedEventArgs.FaultCode</c>.</summary>
    public const string ActivationTimeoutCode = "ActivationTimeout";

    public ProcessLoopbackActivationTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Timing and format policy of one activation. Injected so the timeout can be
/// shortened for tests and so the candidate ladder is a value instead of a
/// hard-coded list.
/// </summary>
internal sealed class ProcessLoopbackActivationOptions
{
    /// <summary>How long the asynchronous activation may take before it faults.</summary>
    public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Slice between cancellation and timeout checks while waiting.</summary>
    public TimeSpan ActivationPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Formats tried in order after the default render endpoint's mix format.
    /// Null means <see cref="DefaultFormatCandidates"/>.
    /// </summary>
    public IReadOnlyList<WaveFormat>? FormatCandidates { get; init; }

    /// <summary>
    /// The ladder used when no explicit list is given: the mix format first, then
    /// IEEE float 48 kHz, then 16-bit PCM at 48 kHz, 44.1 kHz and 16 kHz. A
    /// process loopback client is not backed by a device and answered several of
    /// these on the machine where this was measured, so the ladder is what turns
    /// "one unsupported format" into "still works".
    /// </summary>
    public static IReadOnlyList<WaveFormat> DefaultFormatCandidates { get; } =
    [
        new WaveFormatExtensible(48_000, 32, 2),
        new WaveFormatExtensible(48_000, 16, 2),
        new WaveFormatExtensible(44_100, 16, 2),
        new WaveFormatExtensible(16_000, 16, 1)
    ];

    public static ProcessLoopbackActivationOptions Default { get; } = new();

    /// <summary>The ladder this instance asks for, defaulting to <see cref="DefaultFormatCandidates"/>.</summary>
    public IReadOnlyList<WaveFormat> Candidates => FormatCandidates ?? DefaultFormatCandidates;
}
