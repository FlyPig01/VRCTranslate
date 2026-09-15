using System.ComponentModel;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The polling policy of the VRChat monitor: immediate first scan, three second
/// rhythm, deduplication, confirmation of a changed decision, PID reuse, refused
/// scans, cancellation and release. Every scan and every delay is scripted, so no
/// test waits for a real three second interval.
/// </summary>
public sealed class VrchatProcessWatcherTests
{
    private const int CurrentSession = 1;
    private static readonly DateTimeOffset Origin = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity First = new(4_242, Origin);

    [Fact]
    public void The_default_policy_polls_every_three_seconds()
    {
        var options = VrchatProcessWatcherOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(3), options.PollInterval);
        // 复核必须比轮询短：它只吸收抖动，不能把发现延迟推过一个节拍。
        Assert.InRange(options.ConfirmationDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_first_scan_runs_immediately_and_then_waits_three_seconds()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan());
        await using var harness = new WatcherHarness(source);

        harness.Watcher.Start();

        // 立即扫描一次，随后才请求 3 秒节拍；目标缺席时不需要复核。
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未进入轮询等待");
        Assert.Equal(1, source.ScanCount);
        Assert.Equal(new[] { WatcherHarness.PollInterval }, harness.Delay.Requested);
        Assert.Empty(harness.Published);
    }

    [Fact]
    public async Task An_already_running_vrchat_is_published_after_one_recheck()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan(First));
        await using var harness = new WatcherHarness(source);

        harness.Watcher.Start();
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未请求复核");
        Assert.Equal(1, source.ScanCount);
        Assert.Empty(harness.Published);

        await harness.ReleaseConfirmationAsync();

        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "已运行的 VRChat 未被采纳");
        Assert.Equal(new[] { WatcherHarness.ConfirmationDelay, WatcherHarness.PollInterval }, harness.Delay.Requested);
        Assert.Equal(First, Assert.Single(harness.Published));
        Assert.Equal(2, source.ScanCount);
    }

    [Fact]
    public async Task An_unchanged_target_is_published_once()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan(First));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "首次目标未发布");

        await harness.ReleasePollAsync();
        await harness.ReleasePollAsync();

        await ProcessTestWait.UntilAsync(() => source.ScanCount >= 4, "轮询未继续");
        Assert.Equal(1, harness.PublishedCount);
        Assert.Equal(
            new[] { WatcherHarness.ConfirmationDelay, WatcherHarness.PollInterval, WatcherHarness.PollInterval, WatcherHarness.PollInterval },
            harness.Delay.Requested);
    }

    [Fact]
    public async Task A_changed_decision_is_confirmed_before_it_is_published()
    {
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan())
            .Returns(Scan(First))
            .Returns(Scan(First));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();

        await harness.ReleasePollAsync();
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未请求复核");

        // 复核完成之前什么都不发布。
        Assert.Empty(harness.Published);
        Assert.Equal(WatcherHarness.ConfirmationDelay, harness.Delay.LastRequested);

        await harness.ReleaseConfirmationAsync();

        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "复核后的目标未发布");
        Assert.Equal(WatcherHarness.PollInterval, harness.Delay.Requested[0]);
        Assert.Equal(WatcherHarness.ConfirmationDelay, harness.Delay.Requested[1]);
        Assert.Equal(First, Assert.Single(harness.Published));
        Assert.Equal(3, source.ScanCount);
    }

    [Fact]
    public async Task A_candidate_that_disappears_during_the_recheck_is_never_published()
    {
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan())
            .Returns(Scan(First))
            .Returns(Scan());
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();

        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();

        // 启动器抖动被抑制：协调器没有收到任何切换请求，监视器继续轮询。
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未继续轮询");
        Assert.Empty(harness.Published);
        Assert.Equal(3, source.ScanCount);
        Assert.Equal(WatcherHarness.PollInterval, harness.Delay.LastRequested);
    }

    [Fact]
    public async Task A_reused_process_id_with_a_new_start_time_is_published_again()
    {
        var restarted = new ProcessIdentity(First.ProcessId, Origin.AddMinutes(30));
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan(First))
            .Returns(Scan(First))
            .Returns(Scan(restarted));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "首次目标未发布");

        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();

        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 2, "PID 复用未被当作新目标");
        Assert.Equal(restarted, harness.Published[1]);
        Assert.False(First.Matches(harness.Published[1]));
        Assert.Equal(restarted, harness.Watcher.Current);
    }

    [Fact]
    public async Task A_target_that_exits_is_published_as_no_target()
    {
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan(First))
            .Returns(Scan(First))
            .Returns(Scan());
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "首次目标未发布");

        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();

        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 2, "退出未发布空目标");
        Assert.Null(harness.Published[1]);
        Assert.Null(harness.Watcher.Current);
    }

    [Fact]
    public async Task An_unreadable_start_time_keeps_matching_by_process_id()
    {
        // StartTime 读取竞态：读不到就只按 PID 比较，同一次运行不会被重复发布。
        var unreadable = new ProcessIdentity(First.ProcessId);
        var source = new ScriptedVrchatProcessSource().Returns(Scan(unreadable));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "首次目标未发布");

        await harness.ReleasePollAsync();

        await ProcessTestWait.UntilAsync(() => source.ScanCount >= 3, "轮询未继续");
        Assert.Equal(1, harness.PublishedCount);
        Assert.Equal(First.ProcessId, harness.Watcher.Current!.ProcessId);
        Assert.Null(harness.Watcher.Current.StartTimeUtc);
    }

    [Fact]
    public async Task A_scan_that_is_refused_does_not_end_the_monitoring()
    {
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan())
            .Throws(new Win32Exception(5, "拒绝访问。"))
            .Returns(Scan(First))
            .Returns(Scan(First));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();

        await harness.ReleasePollAsync();
        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();

        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "拒绝访问后监视器未恢复");
        Assert.Equal(4, source.ScanCount);
        Assert.True(harness.Watcher.IsRunning);
    }

    [Fact]
    public async Task Starting_twice_runs_one_loop()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan(First));
        await using var harness = new WatcherHarness(source);

        harness.Watcher.Start();
        harness.Watcher.Start();

        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未进入复核等待");
        Assert.Equal(1, source.ScanCount);

        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(() => harness.PublishedCount == 1, "首次目标未发布");
        Assert.Equal(2, source.ScanCount);
        Assert.True(harness.Watcher.IsRunning);
    }

    [Fact]
    public async Task Stop_ends_the_loop_and_a_later_start_scans_again()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan(First));
        await using var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未请求复核");

        await harness.Watcher.StopAsync();
        await harness.Watcher.StopAsync();

        Assert.False(harness.Watcher.IsRunning);
        Assert.Equal(1, harness.Delay.CancelledCount);

        harness.Watcher.Start();
        await ProcessTestWait.UntilAsync(() => source.ScanCount == 2, "重新开始后未立即扫描");
    }

    [Fact]
    public async Task Dispose_cancels_the_pending_wait_and_leaves_no_polling_task()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan());
        var harness = new WatcherHarness(source);
        harness.Watcher.Start();
        await ProcessTestWait.UntilAsync(() => harness.Delay.WaitingCount == 1, "监视器未进入轮询等待");
        var scansBeforeRelease = source.ScanCount;

        await harness.Watcher.DisposeAsync();
        await harness.Watcher.DisposeAsync();

        Assert.False(harness.Watcher.IsRunning);
        Assert.Equal(1, harness.Delay.CancelledCount);
        await Task.Delay(60);
        // 释放后不再有任何轮询：计数停在释放前的那一次。
        Assert.Equal(scansBeforeRelease, source.ScanCount);
        Assert.Throws<ObjectDisposedException>(() => harness.Watcher.Start());
    }

    private static VrchatProcessScan Scan(params ProcessIdentity[] identities) =>
        new(
            [.. identities.Select(identity => new VrchatProcessCandidate(identity, CurrentSession, HasForegroundWindow: false))],
            CurrentSession);
}
