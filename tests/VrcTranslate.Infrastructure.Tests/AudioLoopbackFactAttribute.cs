using NAudio.CoreAudioApi;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Runs the Application Loopback integration tests only where they can really
/// prove something: Windows Build 20348 or later and an active render endpoint.
/// Anywhere else they are reported as skipped with the concrete reason, so a
/// silent machine can never turn into a silent pass.
/// </summary>
public sealed class AudioLoopbackFactAttribute : FactAttribute
{
    public AudioLoopbackFactAttribute()
    {
        if (!AudioLoopbackIntegrationEnvironment.TryPrepare(out var reason)) Skip = reason;
    }
}

/// <summary>Environment probe behind <see cref="AudioLoopbackFactAttribute"/>.</summary>
internal static class AudioLoopbackIntegrationEnvironment
{
    /// <summary>Decides whether the real process loopback path may be exercised.</summary>
    public static bool TryPrepare(out string reason)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                reason = "进程回环集成测试只在 Windows 上运行。";
                return false;
            }

            var support = WindowsProcessLoopbackSupport.Instance;
            if (!support.IsSupported)
            {
                reason = "进程回环需要 Windows 10 Build "
                    + $"{WindowsProcessLoopbackSupport.MinimumBuild} 或更高版本，当前 Build 为 "
                    + $"{WindowsProcessLoopbackSupport.DescribeBuild(support.Build)}。";
                return false;
            }

            if (!TryProbeRenderEndpoint(out reason)) return false;

            if (!DotnetHost.TryLocate(out _))
            {
                reason = "找不到 dotnet 主机，无法启动受控测试音进程。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = $"音频回环集成测试的环境探测失败：{exception.GetType().Name} {exception.Message}";
            return false;
        }
    }

    private static bool TryProbeRenderEndpoint(out string reason)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.State != DeviceState.Active)
            {
                reason = "默认播放端点没有处于活动状态，无法捕获测试音。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = $"没有可用的默认播放端点：{exception.GetType().Name} {exception.Message}";
            return false;
        }
    }
}
