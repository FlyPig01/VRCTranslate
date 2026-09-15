using System.Diagnostics;
using System.Runtime.InteropServices;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>One platform scan: every VRChat candidate plus the session to scope it to.</summary>
public sealed record VrchatProcessScan(
    IReadOnlyList<VrchatProcessCandidate> Candidates,
    int? CurrentSessionId)
{
    /// <summary>A scan without candidates; also the answer on a platform that cannot inspect processes.</summary>
    public static VrchatProcessScan None { get; } = new([], null);
}

/// <summary>
/// Enumerates the VRChat processes of one scan. The Windows implementation walks
/// <see cref="Process"/>; tests script the answer, so the polling policy can be
/// proven without a machine that happens to run VRChat.
/// </summary>
public interface IVrchatProcessSource
{
    VrchatProcessScan Scan();
}

/// <summary>
/// Answers "which VRChat instance should the capture follow right now" by feeding
/// a single scan into the Phase 1 policy <see cref="VrchatInstanceSelector"/>.
/// Keeping that call in one place is what makes a one-shot lookup and the polling
/// monitor agree, and VRChat being the fixed product target means nothing here
/// reads or writes user settings.
/// </summary>
public sealed class VrchatProcessResolver : IProcessTargetResolver
{
    /// <summary>Process name the platform is asked for: VRChat, never "VRChat.exe".</summary>
    public const string DefaultProcessName = "VRChat";

    private readonly IVrchatProcessSource _source;

    public VrchatProcessResolver(IVrchatProcessSource? source = null) =>
        _source = source ?? new WindowsVrchatProcessSource();

    /// <summary>
    /// Brings a process name into the form <see cref="Process.GetProcessesByName(string)"/>
    /// expects: trimmed, without the ".exe" suffix and never empty.
    /// </summary>
    public static string NormalizeProcessName(string? processName)
    {
        var name = processName?.Trim();
        if (string.IsNullOrEmpty(name)) return DefaultProcessName;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].Trim();
        return name.Length == 0 ? DefaultProcessName : name;
    }

    /// <summary>The VRChat instance of a fresh scan, or null when none is running.</summary>
    public ProcessIdentity? Resolve() => Resolve(currentTarget: null);

    /// <summary>
    /// Picks the target of one fresh scan. <paramref name="currentTarget"/> is the
    /// instance the capture already follows, so a living instance survives an
    /// unrelated scan instead of being re-decided on every poll.
    /// </summary>
    public ProcessIdentity? Resolve(ProcessIdentity? currentTarget)
    {
        var scan = _source.Scan();
        return VrchatInstanceSelector.Select(scan.Candidates, scan.CurrentSessionId, currentTarget);
    }
}

/// <summary>
/// Windows scan behind <see cref="Process.GetProcessesByName(string)"/>. Every
/// handle is released as soon as its values were read, and a process that exits
/// or refuses access between enumeration and inspection is skipped instead of
/// failing the scan: the next poll usually sees the real state.
/// </summary>
public sealed class WindowsVrchatProcessSource : IVrchatProcessSource
{
    private readonly string _processName;

    public WindowsVrchatProcessSource(string? processName = null) =>
        _processName = VrchatProcessResolver.NormalizeProcessName(processName);

    /// <summary>The normalized image name this source looks up.</summary>
    public string ProcessName => _processName;

    public VrchatProcessScan Scan()
    {
        if (!OperatingSystem.IsWindows()) return VrchatProcessScan.None;

        var currentSessionId = TryReadCurrentSessionId();
        var foregroundProcessId = TryReadForegroundProcessId();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(_processName);
        }
        catch (Exception)
        {
            // Even the enumeration can be refused. An empty scan keeps the
            // monitor polling instead of ending it.
            return new VrchatProcessScan([], currentSessionId);
        }

        var candidates = new List<VrchatProcessCandidate>(processes.Length);
        foreach (var process in processes)
        {
            // Every Process owns a system handle and is released right here: a
            // watcher that runs for hours must not accumulate handles.
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    candidates.Add(new VrchatProcessCandidate(
                        // An unreadable start time travels as null, and the
                        // identity then falls back to the process id alone.
                        new ProcessIdentity(processId, TryReadStartTime(process)),
                        TryReadSessionId(process),
                        HasForegroundWindow: processId == foregroundProcessId));
                }
                catch (Exception)
                {
                    // The process ended between enumeration and inspection.
                }
            }
        }

        return new VrchatProcessScan(candidates, currentSessionId);
    }

    /// <summary>
    /// Start time of one process, or null when Windows refused access or the
    /// process already ended. Null is not fatal: the identity then compares on
    /// the process id alone, which is the documented fallback.
    /// </summary>
    public static DateTimeOffset? TryReadStartTime(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // Win32Exception: access denied. InvalidOperationException: the
            // process is gone, or this object never referred to one. Each of
            // them only means "start time unknown".
            return null;
        }
    }

    /// <summary>
    /// Session of one process, or null when Windows refused access or the process
    /// is gone. The policy drops a candidate without a readable session, because
    /// capturing another logged-in user's audio is never the intended behaviour.
    /// </summary>
    public static int? TryReadSessionId(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return process.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Session of this process, which is the interactive user's session. Null
    /// makes the policy stop filtering instead of dropping every candidate.
    /// </summary>
    private static int? TryReadCurrentSessionId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Process owning the foreground window, or null when there is none.</summary>
    private static int? TryReadForegroundProcessId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(window, out var processId);
            return processId is > 0 and <= int.MaxValue ? (int)processId : null;
        }
        catch (Exception)
        {
            // The foreground hint is an optimisation: without it the policy
            // keeps the current instance or falls back to the oldest one.
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
