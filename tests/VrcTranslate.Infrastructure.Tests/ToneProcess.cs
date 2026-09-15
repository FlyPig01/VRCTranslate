using System.Diagnostics;
using System.Globalization;
using System.Text;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// One controlled tone process: a second, independent process that renders a
/// steady sine through WASAPI in shared mode (or renders nothing at all), so
/// process loopback can be checked for real instead of against the test host's
/// own audio.
/// </summary>
internal sealed class ToneProcess : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _error = new();
    private bool _disposed;

    private ToneProcess(Process process, ProcessIdentity identity)
    {
        _process = process;
        Identity = identity;
    }

    /// <summary>Stable identity (pid plus start time) of the rendering process.</summary>
    public ProcessIdentity Identity { get; }

    public int ProcessId => _process.Id;

    /// <summary>Starts a process that renders a steady sine of the given frequency.</summary>
    public static ToneProcess StartTone(double frequency) =>
        Start(["--tone", frequency.ToString(CultureInfo.InvariantCulture)]);

    /// <summary>Starts a process that stays alive without rendering any audio.</summary>
    public static ToneProcess StartSilent() => Start(["--silent"]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // A process that ended on its own needs no help.
        }

        try { _process.WaitForExit(5_000); } catch (Exception) { }
        _process.Dispose();
    }

    private static ToneProcess Start(string[] arguments)
    {
        var host = DotnetHost.Locate();
        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(ToneProcess).Assembly.Location);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--seconds");
        startInfo.ArgumentList.Add(TonePlayerProgram.DefaultSeconds.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动测试音进程。");
        var tone = new ToneProcess(process, new ProcessIdentity(process.Id, TryReadStartTime(process)));
        tone.DrainStandardError();
        tone.WaitForReady();
        return tone;
    }

    private static DateTimeOffset? TryReadStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Keeps stderr drained so a failing helper cannot block on a full pipe; the
    /// text is attached to the failure message when the helper never becomes ready.
    /// </summary>
    private void DrainStandardError() =>
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await _process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                lock (_error) _error.Append(text);
            }
            catch (Exception)
            {
                // The process ended; whatever was read is already recorded.
            }
        });

    private void WaitForReady()
    {
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(ReadyTimeout) || read.Result != TonePlayerProgram.ReadyMarker)
        {
            var reason = DescribeFailure();
            Dispose();
            throw new InvalidOperationException($"测试音进程没有就绪：{reason}");
        }
    }

    private string DescribeFailure()
    {
        var exited = false;
        int? exitCode = null;
        try
        {
            exited = _process.HasExited;
            if (exited) exitCode = _process.ExitCode;
        }
        catch (Exception)
        {
            // The state could not be read; the message then only carries stderr.
        }

        lock (_error)
        {
            var state = exited ? $"进程已退出，退出码 {exitCode}" : "进程仍在运行但没有输出";
            return $"{state}；stderr: {_error.ToString().Trim()}";
        }
    }
}

/// <summary>Finds the .NET host used to start a second copy of the test assembly.</summary>
internal static class DotnetHost
{
    private const string HostFileName = "dotnet.exe";

    public static string Locate()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), HostFileName))
            .FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", HostFileName);
        return File.Exists(fallback) ? fallback : throw new InvalidOperationException("找不到 dotnet 主机，无法启动测试音进程。");
    }

    /// <summary>Whether a host could be located; used by the skip probe.</summary>
    public static bool TryLocate(out string path)
    {
        try
        {
            path = Locate();
            return true;
        }
        catch (InvalidOperationException)
        {
            path = string.Empty;
            return false;
        }
    }
}

/// <summary>Collects the mono 16 kHz samples one capture publishes.</summary>
internal sealed class SampleCollector
{
    private readonly List<float> _samples = [];
    private readonly object _sync = new();

    public int Count
    {
        get { lock (_sync) return _samples.Count; }
    }

    public void Add(ReadOnlyMemory<float> samples)
    {
        lock (_sync) _samples.AddRange(samples.ToArray());
    }

    public void Clear()
    {
        lock (_sync) _samples.Clear();
    }

    public float[] Snapshot()
    {
        lock (_sync) return [.. _samples];
    }
}

/// <summary>Frequency and level analysis for the captured test tones.</summary>
internal static class ToneAnalysis
{
    public const int SampleRate = 16_000;

    /// <summary>
    /// Goertzel magnitude of one frequency, normalized so a pure sine of
    /// amplitude A reports about A. A long capture window is what keeps the two
    /// test tones from leaking into each other's bin.
    /// </summary>
    public static double MagnitudeAt(IReadOnlyList<float> samples, double frequency)
    {
        if (samples.Count == 0) return 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double previous = 0;
        double beforePrevious = 0;
        foreach (var sample in samples)
        {
            var current = sample + (coefficient * previous) - beforePrevious;
            beforePrevious = previous;
            previous = current;
        }

        var power = (previous * previous) + (beforePrevious * beforePrevious) - (coefficient * previous * beforePrevious);
        return Math.Sqrt(Math.Max(0, power)) * 2 / samples.Count;
    }

    /// <summary>Root mean square of the captured samples; zero when nothing was captured.</summary>
    public static double Rms(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0) return 0;
        var sum = 0d;
        foreach (var sample in samples) sum += (double)sample * sample;
        return Math.Sqrt(sum / samples.Count);
    }
}
