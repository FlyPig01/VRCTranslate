using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Smoke tests for the Silero VAD gate. They load the real native engine and
/// the bundled model, but only assert on non-speech input: synthetic tones are
/// not speech, so asserting positive detection here would be guesswork.
/// </summary>
public sealed class SileroVadSegmenterTests
{
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
}
