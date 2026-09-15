using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Serializes every test that renders or captures real audio: the machine has
/// one output device and a shared mix, so these tests must not overlap with each
/// other or with the CPU heavy suites running beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioLoopbackCollection
{
    public const string Name = "audio-loopback-integration";
}

/// <summary>
/// Windows Application Loopback end to end: two independent processes render
/// different frequencies, and the process loopback stream has to contain the
/// target's frequency and nothing of the other process. Skipped - never passed
/// silently - when the build is too old or the machine has no active render
/// endpoint.
/// </summary>
[Collection(AudioLoopbackCollection.Name)]
public sealed class ProcessLoopbackIntegrationTests
{
    private const double TargetFrequency = 440;
    private const double OtherFrequency = 880;
    private const double ReplacementFrequency = 660;

    /// <summary>Long enough for a narrow analysis bin, short enough for a fast suite.</summary>
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(2.5);

    [AudioLoopbackFact]
    public async Task Target_process_loopback_carries_the_target_tone_and_not_the_other_process()
    {
        using var target = ToneProcess.StartTone(TargetFrequency);
        using var other = ToneProcess.StartTone(OtherFrequency);

        var targetCapture = await CaptureProcessLoopbackAsync(target.Identity, CaptureWindow);
        Assert.Null(targetCapture.Fault);
        Assert.True(targetCapture.Samples.Length > 0, "目标进程回环没有采集到任何样本。");

        var targetAtTarget = ToneAnalysis.MagnitudeAt(targetCapture.Samples, TargetFrequency);
        var targetAtOther = ToneAnalysis.MagnitudeAt(targetCapture.Samples, OtherFrequency);
        Assert.True(
            targetAtTarget > 0.01,
            $"目标进程回环没有检测到 {TargetFrequency} Hz：幅度 {targetAtTarget:F4}。");
        Assert.True(
            targetAtTarget > targetAtOther * 6,
            $"非目标进程的 {OtherFrequency} Hz 进入了目标回环：目标 {targetAtTarget:F4}，非目标 {targetAtOther:F4}。");

        // The same capture, aimed at the other process, has to see the mirror
        // image: this is what proves the isolation comes from the target pid and
        // not from the tone being the only audio on the device.
        var otherCapture = await CaptureProcessLoopbackAsync(other.Identity, CaptureWindow);
        Assert.Null(otherCapture.Fault);
        Assert.True(otherCapture.Samples.Length > 0, "第二个进程的进程回环没有采集到任何样本。");

        var otherAtOther = ToneAnalysis.MagnitudeAt(otherCapture.Samples, OtherFrequency);
        var otherAtTarget = ToneAnalysis.MagnitudeAt(otherCapture.Samples, TargetFrequency);
        Assert.True(
            otherAtOther > 0.01,
            $"第二个进程回环没有检测到 {OtherFrequency} Hz：幅度 {otherAtOther:F4}。");
        Assert.True(
            otherAtOther > otherAtTarget * 6,
            $"非目标进程的 {TargetFrequency} Hz 进入了第二个回环：目标 {otherAtOther:F4}，非目标 {otherAtTarget:F4}。");
    }

    [AudioLoopbackFact]
    public async Task Target_without_a_render_stream_stays_on_process_loopback_and_delivers_no_system_audio()
    {
        using var other = ToneProcess.StartTone(OtherFrequency);
        using var silent = ToneProcess.StartSilent();

        await using var capture = new AdaptiveLoopbackAudioCapture(new WindowsAudioCaptureFactory());
        var collector = new SampleCollector();
        var states = new List<AudioSourceState>();
        var sync = new object();
        var processLoopbackReached = false;
        Exception? fault = null;

        capture.SamplesReady += (_, args) =>
        {
            lock (sync)
            {
                // Only audio captured after the switch to the process stream can
                // say anything about the silent target.
                if (processLoopbackReached) collector.Add(args.Samples);
            }
        };
        capture.SourceChanged += (_, args) =>
        {
            lock (sync)
            {
                states.Add(args.State);
                if (args.State.Kind != AudioCaptureSourceKind.ProcessLoopback) return;
                processLoopbackReached = true;
                collector.Clear();
            }
        };
        capture.Faulted += (_, args) => fault ??= args.Exception;

        await capture.StartAsync();
        await capture.ApplyTargetAsync(silent.Identity);

        Assert.Null(fault);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, capture.SourceKind);
        await Task.Delay(CaptureWindow);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, capture.SourceKind);

        var samples = collector.Snapshot();
        await capture.StopAsync();

        Assert.Null(fault);
        Assert.Contains(states, state => state.Kind == AudioCaptureSourceKind.ProcessLoopback);
        Assert.DoesNotContain(states, state => state.IsCompatibilityMode);
        var rms = ToneAnalysis.Rms(samples);
        var leakedOther = ToneAnalysis.MagnitudeAt(samples, OtherFrequency);
        Assert.True(rms < 0.005, $"无渲染流的目标回环不是静音：RMS {rms:F4}。");
        Assert.True(leakedOther < 0.005, $"目标回环混入了系统回环的 {OtherFrequency} Hz：幅度 {leakedOther:F4}。");
    }

    [AudioLoopbackFact]
    public async Task System_loopback_still_captures_a_known_tone_after_the_target_ends()
    {
        var target = ToneProcess.StartTone(TargetFrequency);
        var targetCapture = await CaptureProcessLoopbackAsync(target.Identity, CaptureWindow);
        Assert.Null(targetCapture.Fault);
        target.Dispose();

        using var replacement = ToneProcess.StartTone(ReplacementFrequency);
        var systemCapture = await CaptureSystemLoopbackAsync(CaptureWindow);
        Assert.Null(systemCapture.Fault);
        Assert.True(systemCapture.Samples.Length > 0, "目标结束后系统回环没有采集到任何样本。");

        var systemAtReplacement = ToneAnalysis.MagnitudeAt(systemCapture.Samples, ReplacementFrequency);
        var systemAtTarget = ToneAnalysis.MagnitudeAt(systemCapture.Samples, TargetFrequency);
        Assert.True(
            systemAtReplacement > 0.01,
            $"目标结束后系统回环没有检测到 {ReplacementFrequency} Hz：幅度 {systemAtReplacement:F4}。");
        Assert.True(
            systemAtReplacement > systemAtTarget * 4,
            $"系统回环没有跟上替代进程的测试音：替代 {systemAtReplacement:F4}，旧目标 {systemAtTarget:F4}。");
    }

    private static async Task<CaptureResult> CaptureProcessLoopbackAsync(ProcessIdentity identity, TimeSpan window)
    {
        await using var capture = new ProcessLoopbackAudioCapture(identity);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, capture.SourceKind);
        return await CaptureAsync(capture, window);
    }

    private static async Task<CaptureResult> CaptureSystemLoopbackAsync(TimeSpan window)
    {
        await using var capture = new WindowsAudioCapture(AudioCaptureMode.SystemLoopback);
        return await CaptureAsync(capture, window);
    }

    private static async Task<CaptureResult> CaptureAsync(IAudioCapture capture, TimeSpan window)
    {
        var collector = new SampleCollector();
        Exception? fault = null;
        void OnSamples(object? sender, AudioSamplesEventArgs args) => collector.Add(args.Samples);
        void OnFaulted(object? sender, AudioCaptureFaultedEventArgs args) => fault ??= args.Exception;

        capture.SamplesReady += OnSamples;
        capture.Faulted += OnFaulted;
        try
        {
            await capture.StartAsync();
            await Task.Delay(window);
        }
        finally
        {
            capture.SamplesReady -= OnSamples;
            capture.Faulted -= OnFaulted;
            await capture.StopAsync();
        }

        return new CaptureResult(collector.Snapshot(), fault);
    }

    private readonly record struct CaptureResult(float[] Samples, Exception? Fault);
}
