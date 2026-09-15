using System.Globalization;
using System.Text;
using NAudio.Wave;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>One stream format the activation tried, with the HRESULT it answered.</summary>
internal readonly record struct ProcessLoopbackFormatAttempt(string Format, int HResult, string? Failure);

/// <summary>
/// Everything one activation attempt is diagnosed by. The values are technical
/// only - build, apartment, formats, HRESULTs - so the file can be attached to a
/// bug report without leaking anything the user said or heard.
/// </summary>
internal sealed record ProcessLoopbackDiagnosticsReport
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Apartment the attempt ran in ("MTA"/"STA").</summary>
    public string Apartment { get; init; } = "MTA";

    /// <summary>Operating system build, when the platform could read it.</summary>
    public int? Build { get; init; }

    /// <summary>Default render endpoint mix format, or null when unreadable.</summary>
    public string? DefaultEndpointFormat { get; init; }

    /// <summary>Why the endpoint format could not be read; null when it could.</summary>
    public string? DefaultEndpointFailure { get; init; }

    /// <summary>Immediate HRESULT of <c>ActivateAudioInterfaceAsync</c>.</summary>
    public int? ImmediateHResult { get; init; }

    /// <summary>HRESULT reported by <c>GetActivateResult</c> in the completion callback.</summary>
    public int? ActivateResult { get; init; }

    /// <summary>Candidate formats in the order they were tried.</summary>
    public IReadOnlyList<ProcessLoopbackFormatAttempt> FormatAttempts { get; init; } = [];

    /// <summary>The candidate the client accepted, or null when none was.</summary>
    public string? AcceptedFormat { get; init; }

    /// <summary>Fault code the capture ends up reporting; null on success.</summary>
    public string? FaultCode { get; init; }

    /// <summary>Short failure description for a human reader; null on success.</summary>
    public string? Message { get; init; }

    public bool Succeeded => FaultCode is null;
}

/// <summary>Sink for activation diagnostics. Implementations must never throw.</summary>
/// <remarks>
/// The interface exists so the bounded file writer can be replaced: a test
/// records the reports, and a machine whose data folder is read-only simply
/// loses the log instead of the capture.
/// </remarks>
internal interface IProcessLoopbackDiagnostics
{
    void Report(ProcessLoopbackDiagnosticsReport report);
}

/// <summary>
/// Collects the values of one attempt while it runs, so the factory can write a
/// single report per attempt - including the failed ones, which are the
/// interesting ones.
/// </summary>
internal sealed class ProcessLoopbackActivationTrace
{
    private const int MaxMessageLength = 240;

    private readonly List<ProcessLoopbackFormatAttempt> _attempts = [];

    public ProcessLoopbackActivationTrace(string apartment, int? build)
    {
        Apartment = apartment;
        Build = build;
    }

    public string Apartment { get; }

    public int? Build { get; }

    public string? DefaultEndpointFormat { get; set; }

    /// <summary>Set when the endpoint could not be read; kept out of the format list.</summary>
    public string? DefaultEndpointFailure { get; set; }

    public int? ImmediateHResult { get; set; }

    public int? ActivateResult { get; set; }

    public string? AcceptedFormat { get; set; }

    public string? FaultCode { get; set; }

    public string? Message { get; set; }

    public IReadOnlyList<ProcessLoopbackFormatAttempt> Attempts => _attempts;

    /// <summary>Records one rejected or accepted candidate.</summary>
    public void RecordAttempt(WaveFormat format, int hresult, string? failure) =>
        _attempts.Add(new ProcessLoopbackFormatAttempt(DescribeFormat(format), hresult, failure));

    public ProcessLoopbackDiagnosticsReport ToReport() => new()
    {
        Apartment = Apartment,
        Build = Build,
        DefaultEndpointFormat = DefaultEndpointFormat,
        DefaultEndpointFailure = Truncate(DefaultEndpointFailure),
        ImmediateHResult = ImmediateHResult,
        ActivateResult = ActivateResult,
        FormatAttempts = [.. _attempts],
        AcceptedFormat = AcceptedFormat,
        FaultCode = FaultCode,
        Message = Truncate(Message)
    };

    /// <summary>"48000 Hz/32-bit float/2ch" - the shape the documentation quotes.</summary>
    public static string DescribeFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var standard = AudioSampleConverter.NormalizeFormat(format);
        var kind = standard.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "float",
            WaveFormatEncoding.Pcm => "pcm",
            _ => standard.Encoding.ToString()
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{standard.SampleRate} Hz/{standard.BitsPerSample}-bit {kind}/{standard.Channels}ch");
    }

    private static string? Truncate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var single = message.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxMessageLength ? single : string.Concat(single.AsSpan(0, MaxMessageLength), "…");
    }
}

/// <summary>
/// The activation log in the portable data folder. It is overwritten with a
/// bounded window instead of growing: the last <see cref="MaxLines"/> lines are
/// what a support conversation needs, and a machine that runs for weeks must not
/// accumulate a file nobody reads. Read failures and a read-only folder are
/// swallowed - diagnostics are never allowed to break the capture.
/// </summary>
internal sealed class FileProcessLoopbackDiagnostics : IProcessLoopbackDiagnostics
{
    /// <summary>Lines kept in the file; older activation blocks fall off the top.</summary>
    public const int MaxLines = 200;

    private const int MaxLineLength = 240;

    private readonly Func<string> _pathProvider;
    private readonly object _sync = new();

    /// <summary>The application-wide log next to the other portable data files.</summary>
    public static FileProcessLoopbackDiagnostics Shared { get; } =
        new(static () => PortableStorage.GetPath(AppDataFiles.CaptureDiagnostics));

    /// <summary>Writes to one explicit file; how the tests pin the content and the bound.</summary>
    public FileProcessLoopbackDiagnostics(string path)
        : this(() => path)
    {
    }

    public FileProcessLoopbackDiagnostics(Func<string> pathProvider) =>
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));

    public void Report(ProcessLoopbackDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            var block = Format(report);
            lock (_sync)
            {
                var path = _pathProvider();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var merged = new List<string>(MaxLines);
                if (File.Exists(path)) merged.AddRange(File.ReadAllLines(path));
                merged.AddRange(block);
                if (merged.Count > MaxLines) merged.RemoveRange(0, merged.Count - MaxLines);
                File.WriteAllLines(path, merged);
            }
        }
        catch (Exception)
        {
            // A missing folder, a locked file or a read-only install must cost the
            // log, never the audio capture or the application.
        }
    }

    /// <summary>The lines one report adds; also the unit-tested shape of the file.</summary>
    public static IReadOnlyList<string> Format(ProcessLoopbackDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            Limit(string.Create(
                CultureInfo.InvariantCulture,
                $"[{report.TimestampUtc:yyyy-MM-ddTHH:mm:ss.fffZ}] apartment={report.Apartment} build={Describe(report.Build)} "
                + $"immediate={HResult(report.ImmediateHResult)} activate={HResult(report.ActivateResult)} fault={report.FaultCode ?? "(none)"}"))
        };

        if (report.DefaultEndpointFormat is { } endpoint)
        {
            lines.Add(Limit($"  endpoint={endpoint}"));
        }
        else if (report.DefaultEndpointFailure is { } endpointFailure)
        {
            lines.Add(Limit($"  endpoint=(unreadable) {endpointFailure}"));
        }

        for (var index = 0; index < report.FormatAttempts.Count; index++)
        {
            var attempt = report.FormatAttempts[index];
            var accepted = string.Equals(attempt.Format, report.AcceptedFormat, StringComparison.Ordinal) ? " accepted" : string.Empty;
            var failure = attempt.Failure is { } name ? $" ({name})" : string.Empty;
            lines.Add(Limit($"  candidate[{index}]={attempt.Format} hr={HResult(attempt.HResult)}{failure}{accepted}"));
        }

        if (report.AcceptedFormat is { } acceptedFormat) lines.Add(Limit($"  accepted={acceptedFormat}"));
        if (report.Message is { } message) lines.Add(Limit($"  note={message}"));
        return lines;
    }

    private static string HResult(int? hresult) =>
        hresult is { } value ? string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}") : "(n/a)";

    private static string Describe(int? build) =>
        build is { } value ? value.ToString(CultureInfo.InvariantCulture) : "unknown";

    private static string Limit(string line) =>
        line.Length <= MaxLineLength ? line : string.Concat(line.AsSpan(0, MaxLineLength), "…");
}
