using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The fake chain from monitor to coordinator: a published target really moves
/// the capture onto the VRChat process, a lost target returns to the system mix,
/// and an unsupported process loopback only re-labels the running stream instead
/// of interrupting the half sentence being spoken.
/// </summary>
public sealed class VrchatProcessSwitchingTests
{
    private const int CurrentSession = 1;
    private static readonly DateTimeOffset Origin = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Vrchat = new(4_242, Origin);

    [Fact]
    public async Task A_published_target_switches_the_capture_and_its_exit_switches_back()
    {
        var source = new ScriptedVrchatProcessSource()
            .Returns(Scan())
            .Returns(Scan(Vrchat))
            .Returns(Scan(Vrchat))
            .Returns(Scan())
            .Returns(Scan());
        var factory = new FakeLoopbackCaptureFactory();
        var delay = new ScriptedDelay();
        await using var coordinator = new AdaptiveLoopbackAudioCapture(
            factory,
            new AdaptiveLoopbackOptions { DelayAsync = delay.DelayAsync });
        var changes = new List<AudioSourceChangedEventArgs>();
        coordinator.SourceChanged += (_, args) => { lock (changes) changes.Add(args); };
        await coordinator.StartAsync();
        await using var harness = new WatcherHarness(source, coordinator.ApplyTargetAsync);

        harness.Watcher.Start();
        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(
            () => coordinator.SourceKind == AudioCaptureSourceKind.ProcessLoopback,
            "协调器未切换到 VRChat 进程音频");

        Assert.Equal(Vrchat, coordinator.Target);
        Assert.Equal(Vrchat, coordinator.State.ProcessIdentity);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, factory.Created[^1].Request.SourceKind);
        Assert.Equal(Vrchat, factory.Created[^1].Request.TargetProcess);
        Assert.Equal(1, factory.Created[0].StopCount);
        Assert.Equal(1, factory.Created[0].DisposeCount);
        Assert.True(changes[^1].IsBoundary);

        await harness.ReleasePollAsync();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(
            () => coordinator.SourceKind == AudioCaptureSourceKind.SystemLoopback,
            "VRChat 退出后未切回系统混音");

        Assert.Null(coordinator.Target);
        Assert.Null(coordinator.State.ProcessIdentity);
        Assert.Equal(
            new[]
            {
                AudioCaptureSourceKind.SystemLoopback,
                AudioCaptureSourceKind.ProcessLoopback,
                AudioCaptureSourceKind.SystemLoopback,
            },
            factory.Created.Select(capture => capture.Request.SourceKind));
        // 每次换流都是边界，旧采集实例全部释放，只剩当前这一条在跑。
        Assert.All(changes, change => Assert.True(change.IsBoundary));
        Assert.All(factory.Created.SkipLast(1), capture => Assert.Equal(1, capture.DisposeCount));
        Assert.Equal(0, factory.Created[^1].DisposeCount);
        Assert.True(factory.Created[^1].IsStarted);
    }

    [Fact]
    public async Task An_unsupported_process_loopback_only_relabels_the_running_stream()
    {
        var source = new ScriptedVrchatProcessSource().Returns(Scan(Vrchat));
        var factory = new FakeLoopbackCaptureFactory();
        // Phase 3 之前 Windows 工厂对进程回环请求抛不支持，这正是生产环境的现状。
        factory.MarkUnsupported(AudioCaptureSourceKind.ProcessLoopback);
        await using var coordinator = new AdaptiveLoopbackAudioCapture(factory);
        var changes = new List<AudioSourceChangedEventArgs>();
        var faults = new List<AudioCaptureFaultedEventArgs>();
        coordinator.SourceChanged += (_, args) => { lock (changes) changes.Add(args); };
        coordinator.Faulted += (_, args) => { lock (faults) faults.Add(args); };
        await coordinator.StartAsync();
        var system = Assert.Single(factory.Created);
        await using var harness = new WatcherHarness(source, coordinator.ApplyTargetAsync);

        harness.Watcher.Start();
        await harness.ReleaseConfirmationAsync();
        await ProcessTestWait.UntilAsync(
            () => coordinator.SourceKind == AudioCaptureSourceKind.SystemLoopbackFallback,
            "不支持的进程回环未标记兼容模式");

        Assert.True(coordinator.State.IsCompatibilityMode);
        Assert.Single(factory.Created);
        Assert.Equal(0, system.StopCount);
        Assert.False(changes[^1].IsBoundary);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopbackFallback, changes[^1].State.Kind);
        Assert.True(changes[0].IsBoundary);
        var fault = Assert.Single(faults);
        Assert.IsType<ProcessLoopbackNotSupportedException>(fault.Exception);
    }

    private static VrchatProcessScan Scan(params ProcessIdentity[] identities) =>
        new(
            [.. identities.Select(identity => new VrchatProcessCandidate(identity, CurrentSession, HasForegroundWindow: false))],
            CurrentSession);
}
