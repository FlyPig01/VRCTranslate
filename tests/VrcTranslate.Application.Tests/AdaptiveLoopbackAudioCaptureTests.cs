using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

/// <summary>
/// The switching state machine: no target, target appearing, target leaving,
/// restart, churn, unsupported platform, activation failure, device failure,
/// backoff, candidate failures and concurrent release. Every platform
/// interaction goes through <see cref="FakeAudioCaptureFactory"/>.
/// </summary>
public sealed class AdaptiveLoopbackAudioCaptureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity FirstTarget = new(4_242, Origin);
    private static readonly ProcessIdentity SecondTarget = new(5_000, Origin.AddMinutes(1));
    private static readonly TimeSpan[] Schedule =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    [Fact]
    public async Task Starting_without_a_target_captures_the_system_mix()
    {
        await using var harness = CreateHarness();

        await harness.Capture.StartAsync();

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.False(harness.Capture.State.IsCompatibilityMode);
        var system = Assert.Single(harness.Factory.Created);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, system.Request.SourceKind);
        Assert.True(system.IsStarted);
        var change = Assert.Single(harness.Changes);
        Assert.True(change.IsBoundary);
    }

    [Fact]
    public async Task A_target_switches_to_process_loopback_and_retires_the_old_capture()
    {
        await using var harness = CreateHarness();
        var forwarded = new List<AudioSamplesEventArgs>();
        harness.Capture.SamplesReady += (_, args) => forwarded.Add(args);
        await harness.Capture.StartAsync();
        var system = harness.Factory.Last;

        await harness.Capture.ApplyTargetAsync(FirstTarget);

        var process = harness.Factory.Last;
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, process.Request.SourceKind);
        Assert.Equal(FirstTarget, process.Request.TargetProcess);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, harness.Capture.SourceKind);
        Assert.Equal(FirstTarget, harness.Capture.State.ProcessIdentity);
        Assert.Equal(1, system.StopCount);
        Assert.Equal(1, system.DisposeCount);

        // Audio from the retired capture can no longer reach the recognizer.
        system.Emit([0.5f]);
        Assert.Empty(forwarded);
        process.Emit([0.5f]);
        Assert.Single(forwarded);

        var change = harness.Changes[^1];
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, change.State.Kind);
        Assert.Equal(FirstTarget, change.State.ProcessIdentity);
        Assert.True(change.IsBoundary);
    }

    [Fact]
    public async Task The_old_capture_keeps_working_until_the_candidate_is_up()
    {
        await using var harness = CreateHarness();
        var forwarded = new List<AudioSamplesEventArgs>();
        harness.Capture.SamplesReady += (_, args) => forwarded.Add(args);
        await harness.Capture.StartAsync();
        var system = harness.Factory.Last;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.GateNextStart(gate);

        var switching = harness.Capture.ApplyTargetAsync(FirstTarget);
        await TestWait.UntilAsync(() => harness.Factory.Created.Count == 2, "候选采集器未创建");

        // Not started yet: the running capture still feeds the recognizer.
        system.Emit([0.25f]);
        Assert.Single(forwarded);

        gate.SetResult();
        await switching;
        system.Emit([0.25f]);
        Assert.Single(forwarded);
    }

    [Fact]
    public async Task A_null_target_returns_to_the_plain_system_mix()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var process = harness.Factory.Last;

        await harness.Capture.ApplyTargetAsync(null);

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.Null(harness.Capture.State.ProcessIdentity);
        Assert.False(harness.Capture.State.IsCompatibilityMode);
        Assert.Equal(1, process.StopCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(3, harness.Factory.Created.Count);
        Assert.True(harness.Changes[^1].IsBoundary);
    }

    [Fact]
    public async Task Rapid_target_changes_leave_exactly_one_running_capture()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();

        // A launcher that restarts VRChat, or an instance that keeps flickering,
        // must not leave retired captures or watcher tasks behind.
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        await harness.Capture.ApplyTargetAsync(null);
        await harness.Capture.ApplyTargetAsync(SecondTarget);
        await harness.Capture.ApplyTargetAsync(null);

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.Null(harness.Capture.State.ProcessIdentity);
        Assert.Equal(5, harness.Factory.Created.Count);
        Assert.All(
            harness.Factory.Created.SkipLast(1),
            created => Assert.Equal(1, created.DisposeCount));
        Assert.Single(harness.Factory.Created, created => created.IsStarted && created.DisposeCount == 0);
    }

    [Fact]
    public async Task A_failed_start_is_reported_and_can_be_retried()
    {
        await using var harness = CreateHarness();
        harness.Factory.FailKind(AudioCaptureSourceKind.SystemLoopback, new InvalidOperationException("没有输出设备"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Capture.StartAsync());
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);

        // The device came back: the same coordinator starts normally.
        harness.Factory.HealKind(AudioCaptureSourceKind.SystemLoopback);
        await harness.Capture.StartAsync();

        Assert.True(harness.Factory.Last.IsStarted);
    }

    [Fact]
    public async Task The_same_target_is_not_switched_again()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var changes = harness.Changes.Count;

        await harness.Capture.ApplyTargetAsync(FirstTarget);
        await harness.Capture.ApplyTargetAsync(FirstTarget);

        Assert.Equal(2, harness.Factory.Created.Count);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Equal(changes, harness.Changes.Count);
    }

    [Fact]
    public async Task A_restarted_instance_with_the_same_process_id_is_a_new_target()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var restarted = new ProcessIdentity(FirstTarget.ProcessId, Origin.AddSeconds(45));

        await harness.Capture.ApplyTargetAsync(restarted);

        Assert.Equal(3, harness.Factory.Created.Count);
        Assert.Equal(restarted, harness.Factory.Last.Request.TargetProcess);
        Assert.Equal(restarted, harness.Capture.State.ProcessIdentity);
        Assert.Equal(2, harness.Factory.ProcessAttempts);
    }

    [Fact]
    public async Task An_unsupported_platform_keeps_system_audio_and_stops_attempting()
    {
        await using var harness = CreateHarness();
        harness.Factory.MarkUnsupported(AudioCaptureSourceKind.ProcessLoopback);
        await harness.Capture.StartAsync();

        await harness.Capture.ApplyTargetAsync(FirstTarget);

        Assert.Equal(AudioCaptureSourceKind.SystemLoopbackFallback, harness.Capture.SourceKind);
        Assert.True(harness.Capture.State.IsCompatibilityMode);
        var fault = Assert.Single(harness.Faults);
        Assert.IsType<ProcessLoopbackNotSupportedException>(fault.Exception);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Empty(harness.Delay.Requested);

        // A different instance on the same unsupported platform is not retried either.
        await harness.Capture.ApplyTargetAsync(SecondTarget);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Empty(harness.Delay.Requested);
    }

    [Fact]
    public async Task A_failed_activation_keeps_system_audio_and_backs_off()
    {
        await using var harness = CreateHarness();
        harness.Factory.FailKind(AudioCaptureSourceKind.ProcessLoopback, new InvalidOperationException("激活失败"));
        await harness.Capture.StartAsync();
        var system = harness.Factory.Last;

        await harness.Capture.ApplyTargetAsync(FirstTarget);

        Assert.Equal(AudioCaptureSourceKind.SystemLoopbackFallback, harness.Capture.SourceKind);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Equal(1, harness.Factory.Last.DisposeCount);
        Assert.Equal(0, system.StopCount);
        Assert.Single(harness.Faults);
        Assert.Single(harness.Delay.Requested);
        Assert.Equal(TimeSpan.FromSeconds(5), harness.Delay.Requested[0]);

        // A later scan for the same instance stays inside the backoff window.
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        Assert.Equal(1, harness.Factory.ProcessAttempts);

        // The retry re-attempts by itself and doubles the wait up to the cap.
        Assert.True(harness.Delay.CompleteNext());
        await TestWait.UntilAsync(() => harness.Factory.ProcessAttempts == 2, "第一次重试未发生");
        await TestWait.UntilAsync(() => harness.Delay.Requested.Count == 2, "第一次重试未重新退避");
        Assert.Equal(TimeSpan.FromSeconds(15), harness.Delay.Requested[1]);

        Assert.True(harness.Delay.CompleteNext());
        await TestWait.UntilAsync(() => harness.Delay.Requested.Count == 3, "第二次重试未重新退避");
        Assert.Equal(TimeSpan.FromSeconds(30), harness.Delay.Requested[2]);

        Assert.True(harness.Delay.CompleteNext());
        await TestWait.UntilAsync(() => harness.Delay.Requested.Count == 4, "第三次重试未重新退避");
        Assert.Equal(TimeSpan.FromSeconds(30), harness.Delay.Requested[3]); // capped

        // Only the first failure of the run is announced to the shell.
        Assert.Single(harness.Faults);
        Assert.Equal(0, system.StopCount);
    }

    [Fact]
    public async Task Clearing_the_backoff_allows_an_immediate_attempt()
    {
        await using var harness = CreateHarness();
        harness.Factory.FailKind(AudioCaptureSourceKind.ProcessLoopback, new InvalidOperationException("激活失败"));
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Single(harness.Delay.Requested);

        // The output device changed: the failure history no longer applies.
        harness.Capture.ResetBackoff();
        await harness.Capture.ApplyTargetAsync(FirstTarget);

        Assert.Equal(2, harness.Factory.ProcessAttempts);
    }

    [Fact]
    public async Task A_failed_candidate_keeps_the_running_process_capture()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var running = harness.Factory.Last;
        var changes = harness.Changes.Count;
        harness.Factory.FailKind(AudioCaptureSourceKind.ProcessLoopback, new InvalidOperationException("第二个实例激活失败"));

        await harness.Capture.ApplyTargetAsync(SecondTarget);

        Assert.Equal(0, running.StopCount);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, harness.Capture.SourceKind);
        Assert.Equal(FirstTarget, harness.Capture.State.ProcessIdentity);
        Assert.Equal(changes, harness.Changes.Count);
        Assert.Equal(2, harness.Factory.ProcessAttempts);
        Assert.Single(harness.Delay.Requested);
    }

    [Fact]
    public async Task A_device_failure_rebuilds_the_same_process_source()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var broken = harness.Factory.Last;

        broken.RaiseFault(new InvalidOperationException("设备失效"));

        await TestWait.UntilAsync(() => harness.Factory.ProcessAttempts == 2, "未重建进程回环采集");
        var rebuilt = harness.Factory.Last;
        Assert.True(rebuilt.IsStarted);
        Assert.Equal(FirstTarget, rebuilt.Request.TargetProcess);
        Assert.Equal(1, broken.StopCount);
        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, harness.Capture.SourceKind);
        Assert.Equal(FirstTarget, harness.Capture.State.ProcessIdentity);
        Assert.Empty(harness.Faults); // A recovery the user does not have to know about.
        Assert.True(harness.Changes[^1].IsBoundary);
    }

    [Fact]
    public async Task A_device_failure_falls_back_to_system_audio_when_the_rebuild_fails()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var broken = harness.Factory.Last;
        harness.Factory.FailKind(AudioCaptureSourceKind.ProcessLoopback, new InvalidOperationException("重建失败"));

        broken.RaiseFault(new InvalidOperationException("设备失效"));

        await TestWait.UntilAsync(
            () => harness.Capture.SourceKind == AudioCaptureSourceKind.SystemLoopbackFallback,
            "未降级到系统回环");
        Assert.True(harness.Capture.State.IsCompatibilityMode);
        Assert.Single(harness.Faults);
        Assert.Single(harness.Delay.Requested);
        Assert.Equal(TimeSpan.FromSeconds(5), harness.Delay.Requested[0]);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopbackFallback, harness.Factory.Last.Request.SourceKind);
    }

    [Fact]
    public async Task A_system_fault_is_reported_only_when_it_cannot_be_rebuilt()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        var broken = harness.Factory.Last;
        harness.Factory.FailKind(AudioCaptureSourceKind.SystemLoopback, new InvalidOperationException("没有输出设备"));

        broken.RaiseFault(new InvalidOperationException("设备失效"));

        await TestWait.UntilAsync(() => harness.Faults.Count == 1, "未上报系统采集故障");
        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.Empty(harness.Delay.Requested);
    }

    [Fact]
    public async Task A_rebuilt_system_capture_replaces_the_broken_one()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        var broken = harness.Factory.Last;

        broken.RaiseFault(new InvalidOperationException("设备失效"));

        await TestWait.UntilAsync(() => harness.Factory.Created.Count == 2, "未重建系统回环采集");
        Assert.True(harness.Factory.Last.IsStarted);
        Assert.Equal(1, broken.DisposeCount);
        Assert.Empty(harness.Faults);
        Assert.True(harness.Changes[^1].IsBoundary);
    }

    [Fact]
    public async Task A_fault_from_a_retired_capture_is_ignored()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        var retired = harness.Factory.Last;
        await harness.Capture.ApplyTargetAsync(FirstTarget);

        // The old capture already lost the race; its report must not retire the
        // capture that replaced it.
        retired.RaiseFault(new InvalidOperationException("旧来源故障"));

        await Task.Delay(100);
        Assert.Equal(1, harness.Factory.ProcessAttempts);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, harness.Capture.SourceKind);
        Assert.Equal(0, harness.Factory.Last.DisposeCount);
    }

    [Fact]
    public async Task An_unexpected_stop_recovers_like_a_device_failure()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);
        var stopped = harness.Factory.Last;

        stopped.RaiseStopped();

        await TestWait.UntilAsync(() => harness.Factory.ProcessAttempts == 2, "意外停止后未重建采集");
        Assert.Equal(1, stopped.DisposeCount);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, harness.Capture.SourceKind);
    }

    [Fact]
    public async Task A_stale_candidate_never_replaces_a_newer_decision()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        var system = harness.Factory.Last;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.GateNextStart(gate);

        var first = harness.Capture.ApplyTargetAsync(FirstTarget);
        await TestWait.UntilAsync(() => harness.Factory.Created.Count == 2, "候选采集器未创建");
        var candidate = harness.Factory.Last;

        // The monitor reports that VRChat is gone while activation is still running.
        var second = harness.Capture.ApplyTargetAsync(null);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, candidate.DisposeCount);
        Assert.Equal(0, candidate.StopCount);
        Assert.Equal(0, system.StopCount);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.DoesNotContain(
            harness.Changes,
            change => change.State.Kind == AudioCaptureSourceKind.ProcessLoopback);
    }

    [Fact]
    public async Task Stop_and_dispose_release_every_capture_once()
    {
        var harness = CreateHarness();
        var capture = harness.Capture;
        await capture.StartAsync();
        await capture.ApplyTargetAsync(FirstTarget);

        await Task.WhenAll(capture.StopAsync(), capture.DisposeAsync().AsTask(), capture.StopAsync());

        Assert.All(
            harness.Factory.Created,
            created =>
            {
                Assert.InRange(created.StopCount, 0, 1);
                Assert.InRange(created.DisposeCount, 0, 1);
            });
        Assert.Equal(1, harness.Factory.Created[^1].DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.ApplyTargetAsync(null));
    }

    [Fact]
    public async Task A_stopped_capture_restarts_on_the_system_mix()
    {
        await using var harness = CreateHarness();
        await harness.Capture.StartAsync();
        await harness.Capture.ApplyTargetAsync(FirstTarget);

        await harness.Capture.StopAsync();
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);

        await harness.Capture.StartAsync();

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, harness.Capture.SourceKind);
        Assert.True(harness.Factory.Last.IsStarted);
        // Start, the process switch, and the fresh system capture after stopping.
        Assert.Equal(3, harness.Factory.Created.Count);
    }

    private static Harness CreateHarness()
    {
        var factory = new FakeAudioCaptureFactory();
        var delay = new RecordingDelay();
        var changes = new List<AudioSourceChangedEventArgs>();
        var faults = new List<AudioCaptureFaultedEventArgs>();
        var capture = new AdaptiveLoopbackAudioCapture(
            factory,
            new AdaptiveLoopbackOptions
            {
                FailureBackoff = Schedule,
                DelayAsync = delay.DelayAsync,
            });
        capture.SourceChanged += (_, args) => { lock (changes) changes.Add(args); };
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };
        return new Harness(capture, factory, delay, changes, faults);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            AdaptiveLoopbackAudioCapture capture,
            FakeAudioCaptureFactory factory,
            RecordingDelay delay,
            List<AudioSourceChangedEventArgs> changes,
            List<AudioCaptureFaultedEventArgs> faults)
        {
            Capture = capture;
            Factory = factory;
            Delay = delay;
            Changes = changes;
            Faults = faults;
        }

        public AdaptiveLoopbackAudioCapture Capture { get; }

        public FakeAudioCaptureFactory Factory { get; }

        public RecordingDelay Delay { get; }

        public List<AudioSourceChangedEventArgs> Changes { get; }

        public List<AudioCaptureFaultedEventArgs> Faults { get; }

        public ValueTask DisposeAsync() => Capture.DisposeAsync();
    }
}
