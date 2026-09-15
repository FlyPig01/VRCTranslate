using System.Diagnostics;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The resolver: process name normalization, the multi-instance decision table of
/// Phase 1 reused unchanged, and the resilience of the real Windows scan against
/// processes that are gone or refuse access.
/// </summary>
public sealed class VrchatProcessResolverTests
{
    private const int CurrentSession = 1;
    private static readonly DateTimeOffset Origin = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("VRChat", "VRChat")]
    [InlineData("VRChat.exe", "VRChat")]
    [InlineData("vrchat.EXE", "vrchat")]
    [InlineData("  VRChat.exe  ", "VRChat")]
    [InlineData(".exe", "VRChat")]
    [InlineData("", "VRChat")]
    [InlineData("   ", "VRChat")]
    [InlineData(null, "VRChat")]
    public void A_process_name_is_normalized_without_the_executable_suffix(string? name, string expected) =>
        Assert.Equal(expected, VrchatProcessResolver.NormalizeProcessName(name));

    [Fact]
    public void The_windows_source_looks_up_the_normalized_image_name()
    {
        Assert.Equal(VrchatProcessResolver.DefaultProcessName, new WindowsVrchatProcessSource().ProcessName);
        Assert.Equal("VRChat", new WindowsVrchatProcessSource("VRChat.exe").ProcessName);
        Assert.Equal("VRChat", new WindowsVrchatProcessSource("  VRChat.EXE ").ProcessName);
    }

    [Fact]
    public void The_resolver_returns_the_phase_one_policy_for_every_scan()
    {
        var current = Identity(200, 5);
        var table = new (string Label, VrchatProcessScan Scan, ProcessIdentity? Current, int? Expected)[]
        {
            ("没有候选", new([], CurrentSession), null, null),
            ("其他登录会话的实例被丢弃", new([Candidate(100, 0, sessionId: 2)], CurrentSession), null, null),
            ("读不到会话的实例被丢弃", new([Candidate(100, 0, sessionId: null)], CurrentSession), null, null),
            ("无法确定会话时不过滤", new([Candidate(100, 0, sessionId: null)], null), null, 100),
            ("前台实例优先", new([Candidate(100, 0), Candidate(200, 5, foreground: true)], CurrentSession), null, 200),
            ("当前实例存活则保留", new([Candidate(100, 0), Candidate(200, 5)], CurrentSession), current, 200),
            ("当前实例退出后取最早启动", new([Candidate(300, 30), Candidate(100, 10)], CurrentSession), current, 100),
            ("启动时间读不到时按 PID 排序", new([Unknown(500), Unknown(300)], CurrentSession), null, 300),
            ("PID 复用视为新目标", new([Candidate(200, 60)], CurrentSession), current, 200),
        };

        foreach (var (label, scan, currentTarget, expected) in table)
        {
            var resolver = new VrchatProcessResolver(new ScriptedVrchatProcessSource().Returns(scan));

            var decision = resolver.Resolve(currentTarget);

            // 与 Phase 1 的纯函数逐项一致：监视器不会另写一套选择策略。
            Assert.Equal(
                VrchatInstanceSelector.Select(scan.Candidates, scan.CurrentSessionId, currentTarget),
                decision);
            Assert.True(
                expected == decision?.ProcessId,
                $"决策表用例失败：{label}（期望 {expected?.ToString() ?? "null"}，实际 {decision?.ProcessId.ToString() ?? "null"}）");
        }
    }

    [Fact]
    public void A_reused_process_id_carries_the_new_start_time()
    {
        var previous = Identity(200, 5);
        var resolver = new VrchatProcessResolver(
            new ScriptedVrchatProcessSource().Returns(Scan(Candidate(200, 60))));

        var decision = resolver.Resolve(previous);

        Assert.Equal(200, decision!.ProcessId);
        Assert.Equal(Origin.AddSeconds(60), decision.StartTimeUtc);
        Assert.False(previous.Matches(decision));
    }

    [Fact]
    public void The_windows_source_reads_a_real_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = Process.GetCurrentProcess();
        var source = new WindowsVrchatProcessSource(current.ProcessName + ".exe");

        var scan = source.Scan();

        Assert.Equal((int?)current.SessionId, scan.CurrentSessionId);
        var candidate = Assert.Single(scan.Candidates, item => item.Identity.ProcessId == current.Id);
        Assert.Equal((int?)current.SessionId, candidate.SessionId);
        Assert.NotNull(candidate.Identity.StartTimeUtc);
    }

    [Fact]
    public void A_process_object_without_a_process_reports_no_start_time_or_session()
    {
        using var neverStarted = new Process();

        Assert.Null(WindowsVrchatProcessSource.TryReadStartTime(neverStarted));
        Assert.Null(WindowsVrchatProcessSource.TryReadSessionId(neverStarted));
        Assert.Throws<ArgumentNullException>(() => WindowsVrchatProcessSource.TryReadStartTime(null!));
        Assert.Throws<ArgumentNullException>(() => WindowsVrchatProcessSource.TryReadSessionId(null!));
    }

    [Fact]
    public void Reading_process_values_never_throws_for_the_processes_of_this_machine()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 真实枚举里混着受保护进程、刚退出的进程和权限不足的进程；读取必须安静地
        // 返回 null，绝不能把竞态抛给轮询循环。
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                _ = WindowsVrchatProcessSource.TryReadStartTime(process);
                _ = WindowsVrchatProcessSource.TryReadSessionId(process);
            }
        }
    }

    [Fact]
    public void An_unknown_image_name_scans_to_no_candidate()
    {
        var source = new WindowsVrchatProcessSource($"VrchatMissing-{Guid.NewGuid():N}.exe");

        var scan = source.Scan();

        Assert.Empty(scan.Candidates);
    }

    private static ProcessIdentity Identity(int processId, int startedSecondsAfterOrigin = 0) =>
        new(processId, Origin.AddSeconds(startedSecondsAfterOrigin));

    private static VrchatProcessCandidate Candidate(
        int processId,
        int startedSecondsAfterOrigin = 0,
        int? sessionId = CurrentSession,
        bool foreground = false) =>
        new(Identity(processId, startedSecondsAfterOrigin), sessionId, foreground);

    private static VrchatProcessCandidate Unknown(int processId) =>
        new(new ProcessIdentity(processId), CurrentSession, false);

    private static VrchatProcessScan Scan(params VrchatProcessCandidate[] candidates) =>
        new(candidates, CurrentSession);
}
