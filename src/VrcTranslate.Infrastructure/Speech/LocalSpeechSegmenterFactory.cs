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
            // 1.1.5 baseline: six seconds gave the diarization model enough
            // context to find a speaker change inside the segment. The shorter
            // 4s experiment produced more sub-3s segments, which pyannote never
            // splits; do not shorten it without an A/B on real dialogue.
            return new SileroVadSegmenter(model, maxSpeechSeconds: 6f);
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
