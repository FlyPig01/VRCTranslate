using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop;

/// <summary>
/// Bounded, technical log of the caption path. It exists for the "audio is
/// captured but the window is empty" class of reports: each stage records what
/// it saw, so a single support file says whether the silence is in the model,
/// the segmenter, the recognizer, the caption pipeline or the overlay. It never
/// records recognized or translated text - only lengths, counts, states and
/// exceptions.
/// </summary>
internal static class CaptionDiagnostics
{
    private const int MaxLines = 400;
    private const int MaxLineLength = 300;

    private static readonly object Sync = new();
    private static bool _initialized;
    private static string? _path;

    /// <summary>File the user can send; also shown in the self-test failure notice.</summary>
    public static string Path
    {
        get
        {
            if (_path is not null) return _path;
            try
            {
                _path = PortableStorage.GetPath(AppDataFiles.CaptionDiagnostics);
            }
            catch
            {
                // A machine without a writable data folder still gets a usable
                // display path; the log writer itself fails silently.
                _path = AppDataFiles.CaptionDiagnostics;
            }

            return _path;
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                var theme = Microsoft.UI.Xaml.Application.Current?.RequestedTheme.ToString() ?? "unknown";
                AppendLocked(
                [
                    $"==== VRCTranslate {version} ====",
                    $"time={DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}",
                    $"os={RuntimeInformation.OSDescription} build={Environment.OSVersion.Version.Build} " +
                    $"arch={RuntimeInformation.ProcessArchitecture}",
                    $"culture={CultureInfo.CurrentCulture.Name} acp={GetACP()} " +
                    $"dpi_awareness=PerMonitorV2",
                    $"theme={theme} data={PortableStorage.DataDirectory} " +
                    $"portable={!PortableStorage.UsesUserProfile}",
                ]);
            }
            catch
            {
                // Diagnostics must never prevent the application from opening.
            }
        }
    }

    /// <summary>Appends one line; never throws.</summary>
    public static void Write(string message) => WriteLines([message]);

    /// <summary>Appends several lines atomically; never throws.</summary>
    public static void WriteLines(IEnumerable<string> messages)
    {
        lock (Sync)
        {
            try
            {
                AppendLocked(messages.Select(message =>
                    $"{DateTimeOffset.Now:HH:mm:ss.fff} {message.ReplaceLineEndings(" | ")}"));
            }
            catch
            {
                // A read-only or locked data folder costs the log, never the app.
            }
        }
    }

    /// <summary>Records the self-test, including the exception detail that explains a failure.</summary>
    public static void WriteSelfTest(LocalSpeechSelfTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>
        {
            $"self-test modelReady={result.ModelReady} vadUsable={result.VadUsable} " +
            $"asrUsable={result.AsrUsable} elapsedMs={(int)result.Duration.TotalMilliseconds} " +
            $"message={result.Message}",
        };
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            lines.Add($"self-test detail={Truncate(result.Detail)}");
        }

        WriteLines(lines);
    }

    /// <summary>Records an exception with its type, HRESULT and the first lines of its text.</summary>
    public static void WriteException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write($"{context} {exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
              $"message={Truncate(exception.Message)}");
        Write($"{context} stack={Truncate(exception.ToString())}");
    }

    /// <summary>Logs the display scale once a XAML root exists.</summary>
    public static void WriteDisplayScale(double scale, string surface) =>
        Write($"display scale={scale:0.###} surface={surface}");

    private static void AppendLocked(IEnumerable<string> lines)
    {
        var path = Path;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var merged = new List<string>(MaxLines);
        if (File.Exists(path))
        {
            try { merged.AddRange(File.ReadAllLines(path)); }
            catch { merged.Clear(); }
        }

        merged.AddRange(lines.Select(Truncate).Where(line => line.Length > 0));
        if (merged.Count > MaxLines) merged.RemoveRange(0, merged.Count - MaxLines);
        File.WriteAllLines(path, merged);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var single = value.ReplaceLineEndings(" ");
        return single.Length <= MaxLineLength ? single : single[..MaxLineLength] + "…";
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
