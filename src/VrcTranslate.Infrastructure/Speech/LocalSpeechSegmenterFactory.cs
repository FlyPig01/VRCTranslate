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
            // Four seconds shortens the first-caption wait while the local
            // cluster continuity rule keeps same-person fragments together.
            // Different clusters still use independent per-span matching.
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
