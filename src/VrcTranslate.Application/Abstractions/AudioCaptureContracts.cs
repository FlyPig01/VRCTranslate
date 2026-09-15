namespace VrcTranslate.Application.Abstractions;

/// <summary>
/// Where captured audio comes from. The adaptive coordinator only ever reports
/// the three loopback kinds; <see cref="Microphone"/> belongs to own-voice input.
/// </summary>
public enum AudioCaptureSourceKind
{
    /// <summary>A recording endpoint, used for own-voice input.</summary>
    Microphone,

    /// <summary>The Windows output mix; every application on the device is captured.</summary>
    SystemLoopback,

    /// <summary>Only the audio rendered by one target process tree is captured.</summary>
    ProcessLoopback,

    /// <summary>System loopback used because process loopback is unsupported or failed.</summary>
    SystemLoopbackFallback
}

/// <summary>
/// Stable identity of one process: the platform process id together with its
/// start time. A reused id with a different start time is a different target, so
/// a fast restart never inherits the previous capture decision.
/// </summary>
public sealed record ProcessIdentity
{
    public ProcessIdentity(int processId, DateTimeOffset? startTimeUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ProcessId = processId;
        StartTimeUtc = startTimeUtc;
    }

    public int ProcessId { get; }

    /// <summary>Null when the platform refused or failed to read the start time.</summary>
    public DateTimeOffset? StartTimeUtc { get; }

    /// <summary>
    /// Whether both values describe the same process. A known start time that
    /// differs always means a different process; when either side could not read
    /// it, the process id alone decides.
    /// </summary>
    public bool Matches(ProcessIdentity? other)
    {
        if (other is null || other.ProcessId != ProcessId) return false;
        if (StartTimeUtc is null || other.StartTimeUtc is null) return true;
        return StartTimeUtc.Value.UtcDateTime == other.StartTimeUtc.Value.UtcDateTime;
    }
}

/// <summary>What the capture boundary is currently delivering.</summary>
public sealed record AudioSourceState
{
    /// <summary>The state of a capture that never reported a loopback source.</summary>
    public static AudioSourceState SystemLoopback { get; } = new(AudioCaptureSourceKind.SystemLoopback);

    public AudioSourceState(
        AudioCaptureSourceKind kind,
        ProcessIdentity? processIdentity = null,
        string? lastFaultCode = null)
    {
        Kind = kind;
        ProcessIdentity = processIdentity;
        LastFaultCode = lastFaultCode;
    }

    public AudioCaptureSourceKind Kind { get; }

    /// <summary>The instance behind a process loopback source; null otherwise.</summary>
    public ProcessIdentity? ProcessIdentity { get; }

    /// <summary>Most recent fault code, kept for diagnostics; null when none was reported.</summary>
    public string? LastFaultCode { get; }

    /// <summary>True while process loopback is unavailable and system audio is used instead.</summary>
    public bool IsCompatibilityMode => Kind == AudioCaptureSourceKind.SystemLoopbackFallback;
}

/// <summary>A published source change; the state itself never leaves this layer.</summary>
public sealed class AudioSourceChangedEventArgs : EventArgs
{
    public AudioSourceChangedEventArgs(AudioSourceState state, long generation, bool isBoundary)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Generation = generation;
        IsBoundary = isBoundary;
    }

    public AudioSourceState State { get; }

    /// <summary>
    /// Monotonic decision counter. Recognition started under an older generation
    /// must not publish results once a newer one was decided.
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// True when the audio stream behind the state was replaced, so audio that
    /// was captured before this event must not be joined with what follows.
    /// A mere re-labelling (system audio becoming the compatibility fallback)
    /// keeps the same stream and therefore reports false.
    /// </summary>
    public bool IsBoundary { get; }
}

/// <summary>An audio source failed; the message never crosses the platform boundary.</summary>
public sealed class AudioCaptureFaultedEventArgs : EventArgs
{
    public AudioCaptureFaultedEventArgs(
        AudioCaptureSourceKind sourceKind,
        Exception exception,
        string? faultCode = null,
        ProcessIdentity? processIdentity = null)
    {
        SourceKind = sourceKind;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        FaultCode = faultCode;
        ProcessIdentity = processIdentity;
    }

    public AudioCaptureSourceKind SourceKind { get; }

    public ProcessIdentity? ProcessIdentity { get; }

    /// <summary>Platform fault code (for example a WASAPI HRESULT) when one exists.</summary>
    public string? FaultCode { get; }

    public Exception Exception { get; }
}

/// <summary>A capture stopped delivering samples; raised at most once per start.</summary>
public sealed class AudioCaptureStoppedEventArgs : EventArgs
{
    public AudioCaptureStoppedEventArgs(AudioCaptureSourceKind sourceKind, Exception? exception = null)
    {
        SourceKind = sourceKind;
        Exception = exception;
    }

    public AudioCaptureSourceKind SourceKind { get; }

    /// <summary>Set when the platform ended the capture because the device failed.</summary>
    public Exception? Exception { get; }
}

/// <summary>
/// Everything a platform factory needs to create one capture. The target
/// process is a first-class value instead of a pid smuggled through the
/// microphone device id.
/// </summary>
public sealed record AudioCaptureRequest
{
    public AudioCaptureRequest(
        AudioCaptureSourceKind sourceKind,
        ProcessIdentity? targetProcess = null,
        string? microphoneDeviceId = null)
    {
        if (sourceKind == AudioCaptureSourceKind.ProcessLoopback && targetProcess is null)
        {
            throw new ArgumentException("进程回环必须给出目标进程身份。", nameof(targetProcess));
        }

        SourceKind = sourceKind;
        TargetProcess = targetProcess;
        MicrophoneDeviceId = microphoneDeviceId;
    }

    public AudioCaptureSourceKind SourceKind { get; }

    /// <summary>Required for <see cref="AudioCaptureSourceKind.ProcessLoopback"/>.</summary>
    public ProcessIdentity? TargetProcess { get; }

    /// <summary>
    /// A WASAPI endpoint id from <see cref="IAudioDeviceEnumerator"/>, a numeric
    /// Windows wave-in device index from older settings, or <c>default</c>.
    /// </summary>
    public string? MicrophoneDeviceId { get; }

    /// <summary>Legacy capture mode derived from the source kind.</summary>
    public AudioCaptureMode Mode => SourceKind == AudioCaptureSourceKind.Microphone
        ? AudioCaptureMode.Microphone
        : AudioCaptureMode.SystemLoopback;

    public static AudioCaptureRequest Microphone(string? deviceId = null) =>
        new(AudioCaptureSourceKind.Microphone, null, deviceId);

    public static AudioCaptureRequest SystemLoopback() =>
        new(AudioCaptureSourceKind.SystemLoopback);

    public static AudioCaptureRequest SystemLoopbackFallback() =>
        new(AudioCaptureSourceKind.SystemLoopbackFallback);

    public static AudioCaptureRequest ProcessLoopback(ProcessIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new AudioCaptureRequest(AudioCaptureSourceKind.ProcessLoopback, target);
    }
}

/// <summary>
/// Raised when the platform cannot capture one process tree at all (for example
/// Windows builds before 20348). The coordinator treats it as permanent: it
/// keeps system loopback and stops attempting process activation until restart.
/// </summary>
public sealed class ProcessLoopbackNotSupportedException : Exception
{
    public ProcessLoopbackNotSupportedException()
        : base("当前系统不支持进程回环采集。")
    {
    }

    public ProcessLoopbackNotSupportedException(string message)
        : base(message)
    {
    }

    public ProcessLoopbackNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads the VRChat process the capture should follow. VRChat is the fixed
/// product target, so this port never consults <c>WorkspaceSettings</c> and the
/// interface offers no process-name or pid configuration.
/// </summary>
public interface IProcessTargetResolver
{
    /// <summary>
    /// Stable identity of the VRChat instance to capture, or null when VRChat is
    /// not running (or no instance belongs to the current user session).
    /// </summary>
    ProcessIdentity? Resolve();
}
