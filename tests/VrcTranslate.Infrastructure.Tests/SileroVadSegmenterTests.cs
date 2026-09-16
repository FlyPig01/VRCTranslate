using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;
using Xunit.Abstractions;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Smoke tests for the Silero VAD gate. They load the real native engine and
/// the bundled model, but only assert on non-speech input: synthetic tones are
/// not speech, so asserting positive detection here would be guesswork.
/// </summary>
public sealed class SileroVadSegmenterTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private static string? LocateModel()
    {
        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "assets", "models", "speech", "vad", "silero_vad.onnx"));
        return File.Exists(candidate) ? candidate : null;
    }

    private static SileroVadSegmenter? TryCreate()
    {
        var model = LocateModel();
        if (model is null) return null;
        try
        {
            return new SileroVadSegmenter(model);
        }
        catch (DllNotFoundException)
        {
            // Machine without the sherpa native runtime on the probe path.
            return null;
        }
    }

    [Fact]
    public void Silence_and_stationary_noise_stream_without_segments()
    {
        using var segmenter = TryCreate();
        if (segmenter is null) return;

        Assert.Equal(16_000, segmenter.SampleRate);
        Assert.Empty(segmenter.Append(new float[16_000]));

        var noise = new float[16_000];
        var random = new Random(7);
        for (var index = 0; index < noise.Length; index++)
        {
            noise[index] = (float)((random.NextDouble() * 2 - 1) * 0.05);
        }
        Assert.Empty(segmenter.Append(noise));
        Assert.Empty(segmenter.Append(new float[16_000]));

        // Flush force-closes whatever the detector still holds, which for a
        // noise tail can fabricate one segment; the recognizer drops it as
        // empty text. What streams live must stay quiet, though.
        var flushed = segmenter.Flush();
        Assert.True(flushed.Count <= 1);
    }

    [Fact]
    public void Factory_falls_back_to_the_energy_gate_without_the_model()
    {
        // The factory resolves the model from the application base directory,
        // which for tests never contains Models\vad; it must not throw.
        var segmenter = LocalSpeechSegmenterFactory.CreateDefault();
        Assert.IsType<SpeechSegmenter>(segmenter);
    }

    [Fact]
    public void A_continuous_signal_is_force_cut_at_12s_without_eating_speech()
    {
        using var segmenter = TryCreate();
        if (segmenter is null) return;

        // 0.3 × 440 Hz 正弦 + 0.05 白噪声、连续 18 s、无静音。这不是真实语音，
        // 只是为了逼出 12 s 强切；实测该信号在 threshold 0~0.5 下切段结果一致，
        // 因此直接用 TryCreate() 的默认参数。所有容差来自同一份实测标定
        // （docs/archive/D16与D17修复方案.md §2.2）：段长超 704 样本、段间缺口 1344、
        // 首段起点偏移 1280（检测器预热）。喂入按 512 一块且包含尾部余量
        // （探测脚本曾漏喂尾块 256 样本，见方案 §5.2 的勘误）。
        const int sampleRate = 16_000;
        const int totalSamples = 18 * sampleRate;
        var signal = new float[totalSamples];
        var random = new Random(7);
        for (var i = 0; i < totalSamples; i++)
        {
            // 高斯白噪声（Box-Muller，σ=0.05）：探测脚本用的是 standard_normal，
            // 均匀噪声实测会把语音判定提前打断（只出 1 段 1.56 s）。
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var noise = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            signal[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate)
                        + 0.05f * (float)noise;
        }

        var segments = new List<float[]>();
        const int chunkSize = 512;
        for (var offset = 0; offset < totalSamples; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, totalSamples - offset);
            var chunk = new float[size];
            Array.Copy(signal, offset, chunk, 0, size);
            foreach (var segment in segmenter.Append(chunk)) segments.Add(segment.ToArray());
        }
        foreach (var segment in segmenter.Flush()) segments.Add(segment.ToArray());

        var diagnostic = string.Join(", ", segments.Select(segment => $"{segment.Length:N0}"));
        _output.WriteLine($"实测段：{segments.Count} 段 [{diagnostic}]");

        // 18 s 连续信号必须触发 12 s 强切（至少两段），否则强切路径从未被执行。
        Assert.True(segments.Count >= 2, $"18 s 连续信号应至少切成 2 段，实际 {segments.Count} 段（{diagnostic}）。");

        // 段是原始波形的切片拷贝：用 32 样本前缀 + 单调前移游标把每段定位回原始波形
        // （周期信号下更短的前缀会匹配到错误位置，实测踩过，见方案第三版说明）。
        var firstStart = -1;
        var previousEnd = -1;
        var cursor = 0;
        foreach (var segment in segments)
        {
            Assert.True(segment.Length >= 32);
            var start = Locate(signal, segment, cursor);
            Assert.True(start >= 0, "段未能在原始波形中定位（32 样本前缀匹配失败）。");
            if (previousEnd < 0)
            {
                firstStart = start;
                Assert.True(start <= 1920, $"首段起点 {start} 超过预热容差 1920 样本（0.12 s）。");
            }
            else
            {
                var gap = start - previousEnd;
                Assert.True(gap <= 1536, $"强切边界缺口 {gap} 样本超过容差 1536（3 个分析窗）。");
            }

            Assert.True(segment.Length <= 192_000 + 1024, $"段长 {segment.Length} 超过 12 s + 2 窗（193024）。");
            previousEnd = start + segment.Length;
            cursor = start + 1;
        }

        // 末尾缺口与「不明去向样本占比」只记录不断言：实测 0 ~ 1.93 s 波动，
        // 真实语音上的比例以 B2 复测为准（方案 §5.3）。
        var produced = segments.Sum(segment => (long)segment.Length);
        var tailGap = totalSamples - previousEnd;
        var unaccounted = totalSamples - produced;
        _output.WriteLine(
            $"段数 {segments.Count}，首段起点 {firstStart}，产出 {produced} 样本，" +
            $"末尾缺口 {tailGap}，不明去向合计 {unaccounted}（占喂入 {unaccounted * 100.0 / totalSamples:F2}%）。");
    }

    /// <summary>First position at or after <paramref name="from"/> whose 32 samples equal the segment head.</summary>
    private static int Locate(float[] signal, float[] segment, int from)
    {
        for (var position = from; position + 32 <= signal.Length; position++)
        {
            var matched = true;
            for (var k = 0; k < 32; k++)
            {
                if (signal[position + k] != segment[k])
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return position;
        }

        return -1;
    }
}
