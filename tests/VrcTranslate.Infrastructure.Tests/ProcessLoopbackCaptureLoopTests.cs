using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// The capture loop policy of <see cref="ProcessLoopbackAudioCapture"/>, driven
/// by a scripted session: silence is never recognizer input, every acquired
/// packet is released on the same thread, faults surface once, and start and
/// stop are repeatable.
/// </summary>
public sealed class ProcessLoopbackCaptureLoopTests
{
    private static readonly ProcessIdentity Target = new(4_242);

    [Fact]
    public async Task Silent_packets_are_not_delivered_as_audio()
    {
        var session = new ScriptedProcessLoopbackSession(
            ScriptedAudio.FloatFormat,
            [
                new ScriptedStep(ScriptedAudio.Constant(0.9f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.Silent, null),
                new ScriptedStep(ScriptedAudio.Constant(0.2f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.None, null)
            ]);
        var collected = new List<float[]>();
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = CreateCapture(session);
        capture.SamplesReady += (_, args) =>
        {
            lock (collected) collected.Add(args.Samples.ToArray());
            firstSample.TrySetResult();
        };

        await capture.StartAsync();
        await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync();

        lock (collected)
        {
            var samples = Assert.Single(collected);
            Assert.Equal(16_000, capture.SampleRate);
            // 480 frames at 48 kHz become 160 samples at 16 kHz, and the silent
            // packet's 0.9 amplitude must not be the one that arrived.
            Assert.Equal(160, samples.Length);
            Assert.All(samples, sample => Assert.InRange(sample, 0.19f, 0.21f));
        }

        // Both packets were acquired and both were handed back; what the silent
        // one must not do is reach the recognizer.
        Assert.Equal([ScriptedAudio.FramesPerPacket, ScriptedAudio.FramesPerPacket], session.ReleasedFrames);
    }

    [Fact]
    public async Task Discontinuity_packets_still_carry_audio()
    {
        // A gap between packets is not a fault: the packet holds real audio, so
        // it is delivered instead of being dropped with the silent ones.
        var session = new ScriptedProcessLoopbackSession(
            ScriptedAudio.FloatFormat,
            [new ScriptedStep(ScriptedAudio.Constant(0.2f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.DataDiscontinuity, null)]);
        var samples = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = CreateCapture(session);
        capture.SamplesReady += (_, args) => samples.TrySetResult(args.Samples.ToArray());

        await capture.StartAsync();
        var delivered = await samples.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync();

        Assert.Equal(160, delivered.Length);
        Assert.All(delivered, sample => Assert.InRange(sample, 0.19f, 0.21f));
    }

    [Fact]
    public async Task Empty_packets_are_skipped_without_a_release()
    {
        // AUDCLNT_S_BUFFER_EMPTY can win the race against the size query. Such a
        // packet has no buffer at all, so releasing it would be an error.
        var session = new ScriptedProcessLoopbackSession(
            ScriptedAudio.FloatFormat,
            [
                new ScriptedStep(IntPtr.Zero, 0, AudioClientBufferFlags.None, null, AnnouncedFrames: ScriptedAudio.FramesPerPacket),
                new ScriptedStep(ScriptedAudio.Constant(0.2f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.None, null)
            ]);
        var samples = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = CreateCapture(session);
        capture.SamplesReady += (_, args) => samples.TrySetResult(args.Samples.ToArray());

        await capture.StartAsync();
        var delivered = await samples.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.StopAsync();

        Assert.Equal(160, delivered.Length);
        Assert.Equal([ScriptedAudio.FramesPerPacket], session.ReleasedFrames);
    }

    [Fact]
    public async Task Device_invalidation_faults_once_and_stops_once()
    {
        var invalidated = new COMException("设备已失效", unchecked((int)0x88890004));
        var session = new ScriptedProcessLoopbackSession(
            ScriptedAudio.FloatFormat,
            [new ScriptedStep(IntPtr.Zero, 0, AudioClientBufferFlags.None, invalidated, AnnouncedFrames: ScriptedAudio.FramesPerPacket)]);
        var faults = new List<AudioCaptureFaultedEventArgs>();
        var stops = new List<AudioCaptureStoppedEventArgs>();
        await using var capture = CreateCapture(session);
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };
        capture.Stopped += (_, args) => { lock (stops) stops.Add(args); };

        await capture.StartAsync();
        await WaitUntilAsync(() => { lock (stops) return stops.Count > 0; }, "采集循环没有因设备失效而停止");
        await capture.StopAsync();
        await capture.StopAsync();

        var fault = Assert.Single(faults);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, fault.SourceKind);
        Assert.Equal("DeviceInvalidated", fault.FaultCode);
        Assert.Equal(4_242, fault.ProcessIdentity?.ProcessId);
        var stop = Assert.Single(stops);
        Assert.Same(invalidated, stop.Exception);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Capture_can_be_started_again_after_a_stop()
    {
        var factory = new ScriptedProcessLoopbackSessionFactory(
            new ScriptedProcessLoopbackSession(
                ScriptedAudio.FloatFormat,
                [new ScriptedStep(ScriptedAudio.Constant(0.2f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.None, null)]),
            new ScriptedProcessLoopbackSession(
                ScriptedAudio.FloatFormat,
                [new ScriptedStep(ScriptedAudio.Constant(0.4f), ScriptedAudio.FramesPerPacket, AudioClientBufferFlags.None, null)]));
        var delivered = new List<float>();
        var sync = new object();
        await using var capture = new ProcessLoopbackAudioCapture(Target, factory);
        capture.SamplesReady += (_, args) =>
        {
            lock (sync) delivered.Add(args.Samples.Span[0]);
        };

        await capture.StartAsync();
        await WaitUntilAsync(() => { lock (sync) return delivered.Count > 0; }, "第一次启动没有采到样本");
        await capture.StopAsync();

        await capture.StartAsync();
        await WaitUntilAsync(() => { lock (sync) return delivered.Count > 1; }, "第二次启动没有采到样本");
        await capture.StopAsync();

        Assert.Equal(2, factory.OpenCount);
        lock (sync)
        {
            Assert.Equal(0.2f, delivered[0], 3);
            Assert.Equal(0.4f, delivered[1], 3);
        }
    }

    [Fact]
    public async Task Activation_failure_is_reported_without_a_stop_event()
    {
        var failed = new COMException("设备已移除", unchecked((int)0x88890026));
        var factory = new ScriptedProcessLoopbackSessionFactory((_, _) => throw failed);
        var faults = new List<AudioCaptureFaultedEventArgs>();
        var stops = new List<AudioCaptureStoppedEventArgs>();
        await using var capture = new ProcessLoopbackAudioCapture(Target, factory);
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };
        capture.Stopped += (_, args) => { lock (stops) stops.Add(args); };

        var thrown = await Assert.ThrowsAsync<COMException>(() => capture.StartAsync());

        Assert.Same(failed, thrown);
        Assert.Equal("ResourcesInvalidated", Assert.Single(faults).FaultCode);
        // A capture that never delivered audio has not stopped delivering it.
        Assert.Empty(stops);
    }

    [Fact]
    public async Task Cancellation_during_activation_reports_neither_fault_nor_stop()
    {
        var opened = new ManualResetEventSlim(false);
        var factory = new ScriptedProcessLoopbackSessionFactory((_, token) =>
        {
            opened.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("取消后不应继续激活。");
        });
        var faults = new List<AudioCaptureFaultedEventArgs>();
        var stops = new List<AudioCaptureStoppedEventArgs>();
        await using var capture = new ProcessLoopbackAudioCapture(Target, factory);
        capture.Faulted += (_, args) => { lock (faults) faults.Add(args); };
        capture.Stopped += (_, args) => { lock (stops) stops.Add(args); };
        using var cancellation = new CancellationTokenSource();

        var start = capture.StartAsync(cancellation.Token);
        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Empty(faults);
        Assert.Empty(stops);
    }

    [Fact]
    public async Task Start_reports_the_process_loopback_source_once()
    {
        var session = new ScriptedProcessLoopbackSession(ScriptedAudio.FloatFormat, []);
        var changes = new List<AudioSourceChangedEventArgs>();
        await using var capture = CreateCapture(session);
        capture.SourceChanged += (_, args) => changes.Add(args);

        await capture.StartAsync();
        await capture.StopAsync();

        var change = Assert.Single(changes);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, change.State.Kind);
        Assert.Equal(4_242, change.State.ProcessIdentity?.ProcessId);
        // Process loopback is one fixed stream, so starting it is not a boundary.
        Assert.False(change.IsBoundary);
    }

    private static ProcessLoopbackAudioCapture CreateCapture(IProcessLoopbackSession session) =>
        new(Target, new ScriptedProcessLoopbackSessionFactory((_, _) => session));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"等待超时：{because}");
    }
}
