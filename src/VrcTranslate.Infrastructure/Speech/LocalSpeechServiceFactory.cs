using VrcTranslate.Application.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Creates the default local speech service for the desktop host.</summary>
public static class LocalSpeechServiceFactory
{
    public static LocalSpeechService CreateDefault(string? modelDirectory = null, int? threads = null)
    {
        var manager = new LocalSpeechModelManager(modelDirectory);
        var recognizer = new WhisperLocalSpeechRecognizer(manager, threads);
        return new LocalSpeechService(manager, recognizer);
    }
}
