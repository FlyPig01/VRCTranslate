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
            // The native max-speech cut overshoots in practice (measured on the
            // podcast sample: 12s configured -> 14.7s segments; 8s -> 11.4s;
            // 6s -> 9.8s). Six seconds keeps the first continuous segment and
            // the 53.5s target segment short enough to caption while the 16s
            // stall guard still has margin above the observed 9.8s.
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
