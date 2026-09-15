using System.Globalization;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Decides whether this machine can capture the render tree of a single
/// process. The audio factory asks this before it touches any COM object, so a
/// build that is too old is never asked to activate a process loopback stream
/// and cannot fail at it.
/// </summary>
/// <remarks>
/// The decision is injected rather than read inline: the machine running the
/// tests is new enough, so the "too old to try" path can only be proven with a
/// substitute implementation.
/// </remarks>
public interface IProcessLoopbackSupport
{
    /// <summary>True when a process loopback activation may be attempted at all.</summary>
    bool IsSupported { get; }

    /// <summary>Operating system build behind the decision; null when unknown.</summary>
    int? Build { get; }
}

/// <summary>
/// The one place that reads the operating system version. Windows Application
/// Loopback (<c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c>) needs Windows 10
/// Build 20348 or later, which is above this application's own Windows 10 2004
/// (Build 19041) floor: older builds keep using system loopback.
/// </summary>
public sealed class WindowsProcessLoopbackSupport : IProcessLoopbackSupport
{
    /// <summary>First Windows 10 build that provides process loopback activation.</summary>
    public const int MinimumBuild = 20348;

    /// <summary>Shared instance for the composition root.</summary>
    public static WindowsProcessLoopbackSupport Instance { get; } = new();

    public bool IsSupported =>
        OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    public int? Build => OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : null;

    /// <summary>The build as text for a user-facing explanation, "未知" when unreadable.</summary>
    public static string DescribeBuild(int? build) =>
        build is { } value ? value.ToString(CultureInfo.InvariantCulture) : "未知";
}
