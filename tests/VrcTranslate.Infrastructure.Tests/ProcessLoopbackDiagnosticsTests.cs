using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The capture diagnostics log: what it records, that it is bounded instead of
/// growing, and that a data folder which cannot be written costs the log line
/// and never the capture. The log is the channel a distributed build is
/// diagnosed through, because the page only shows the conclusion.
/// </summary>
public sealed class ProcessLoopbackDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrctranslate-capture-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_log_records_the_technical_values_of_one_activation()
    {
        var path = Path.Combine(_directory, "capture-diagnostics.log");
        var log = new FileProcessLoopbackDiagnostics(path);

        log.Report(new ProcessLoopbackDiagnosticsReport
        {
            Apartment = "MTA",
            Build = 26_200,
            DefaultEndpointFormat = "48000 Hz/32-bit float/2ch",
            ImmediateHResult = 0,
            ActivateResult = unchecked((int)0x88890026),
            FaultCode = "ResourcesInvalidated",
            Message = "进程回环激活失败。",
            FormatAttempts =
            [
                new ProcessLoopbackFormatAttempt("48000 Hz/32-bit float/2ch", unchecked((int)0x88890026), null)
            ]
        });

        var text = File.ReadAllText(path);
        Assert.Contains("apartment=MTA", text, StringComparison.Ordinal);
        Assert.Contains("build=26200", text, StringComparison.Ordinal);
        Assert.Contains("immediate=0x00000000", text, StringComparison.Ordinal);
        Assert.Contains("activate=0x88890026", text, StringComparison.Ordinal);
        Assert.Contains("fault=ResourcesInvalidated", text, StringComparison.Ordinal);
        Assert.Contains("endpoint=48000 Hz/32-bit float/2ch", text, StringComparison.Ordinal);
        Assert.Contains("candidate[0]=48000 Hz/32-bit float/2ch hr=0x88890026", text, StringComparison.Ordinal);
        Assert.Contains("note=进程回环激活失败。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_grows_by_one_block_per_activation_and_stays_bounded()
    {
        var path = Path.Combine(_directory, "capture-diagnostics.log");
        var log = new FileProcessLoopbackDiagnostics(path);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            log.Report(new ProcessLoopbackDiagnosticsReport
            {
                Apartment = "MTA",
                Build = 26_200,
                ImmediateHResult = attempt,
                ActivateResult = 0,
                FaultCode = attempt % 2 == 0 ? "ActivationTimeout" : null,
                FormatAttempts =
                [
                    new ProcessLoopbackFormatAttempt("48000 Hz/32-bit float/2ch", 0, null)
                ]
            });
        }

        var lines = File.ReadAllLines(path);
        Assert.True(
            lines.Length <= FileProcessLoopbackDiagnostics.MaxLines,
            $"诊断日志必须限长，实际 {lines.Length} 行。");
        // Overwritten, not appended: the newest activation is in the file and
        // the oldest attempts have fallen off the top.
        Assert.Contains(lines, line => line.Contains("immediate=0x000000C7", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("immediate=0x00000000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_that_cannot_be_written_costs_the_log_not_the_capture()
    {
        var log = new FileProcessLoopbackDiagnostics(() => throw new IOException("数据目录不可写"));

        // Must not throw: diagnostics are best effort by contract.
        log.Report(new ProcessLoopbackDiagnosticsReport { FaultCode = "ActivationTimeout" });
    }

    [Fact]
    public void A_real_activation_writes_its_outcome_to_the_file()
    {
        // A machine with no readable output endpoint whose client refuses every
        // format: the file has to say both, because that is the combination a
        // distributed build is diagnosed from.
        var path = Path.Combine(_directory, "capture-diagnostics.log");
        var sessionFactory = new ProcessLoopbackSessionFactory(new ProcessLoopbackSessionFactoryDependencies(
            new StubProcessLoopbackSupport(isSupported: true, build: 26_200),
            new ScriptedActivator(new object()),
            new ScriptedClientFactory(new ScriptedProcessLoopbackClient(
                rejections: int.MaxValue,
                rejectionHResult: ProcessLoopbackFault.NotImplemented)),
            new StubRenderFormatProvider(format: null),
            ProcessLoopbackActivationOptions.Default,
            new FileProcessLoopbackDiagnostics(path)));

        var exception = Assert.Throws<System.Runtime.InteropServices.COMException>(() =>
            sessionFactory.Open(new ProcessIdentity(4_242), ProcessLoopbackApartment.Multithreaded, CancellationToken.None));

        Assert.Equal(ProcessLoopbackFault.NotImplemented, exception.HResult);
        var text = File.ReadAllText(path);
        Assert.Contains("apartment=MTA", text, StringComparison.Ordinal);
        Assert.Contains("build=26200", text, StringComparison.Ordinal);
        Assert.Contains("immediate=0x00000000", text, StringComparison.Ordinal);
        Assert.Contains("activate=0x00000000", text, StringComparison.Ordinal);
        Assert.Contains("fault=0x80004001", text, StringComparison.Ordinal);
        Assert.Contains("endpoint=(unreadable)", text, StringComparison.Ordinal);
        Assert.Contains("candidate[0]=48000 Hz/32-bit float/2ch hr=0x80004001", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }
    }
}
