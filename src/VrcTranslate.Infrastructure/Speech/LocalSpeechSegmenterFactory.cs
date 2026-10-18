using VrcTranslate.Application.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Picks the speech gate for a capture session: Silero VAD when the bundled
/// model is available, the adaptive energy gate otherwise. A broken or missing
/// VAD payload must degrade to energy gating instead of stopping captions.
/// </summary>
public static class LocalSpeechSegmenterFactory
{
    public static ISpeechSegmenter CreateDefault()
    {
        var model = ResolveVadModelPath();
        if (model is null) return new SpeechSegmenter();
        try
        {
            // The native max-speech cut overshoots in practice. Four seconds
            // keeps the first caption and multi-speaker bursts shorter than the
            // old 6s setting; the 16s stall guard still has ample margin.
            return new SileroVadSegmenter(model, maxSpeechSeconds: 4f);
        }
        catch
        {
            // The energy gate cannot tell game sound from voice, but it keeps
            // the pipeline alive; the failure shows up in the stop summary.
            return new SpeechSegmenter();
        }
    }

    private static string? ResolveVadModelPath()
    {
        var bundled = Path.Combine(
            AppContext.BaseDirectory,
            LocalSpeechModelCatalog.BundledRelativeFolder,
            LocalSpeechModelCatalog.VadDirectory,
            LocalSpeechModelCatalog.VadFileName);
        return File.Exists(bundled) ? bundled : null;
    }
}
