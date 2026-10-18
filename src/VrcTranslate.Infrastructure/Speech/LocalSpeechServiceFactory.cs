using VrcTranslate.Application.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Creates the default local speech service for the desktop host.</summary>
public static class LocalSpeechServiceFactory
{
    public static LocalSpeechService CreateDefault(string? modelDirectory = null)
    {
        var manager = new LocalSpeechModelManager(modelDirectory, validateBundledExactSizes: true);
        var recognizer = new SenseVoiceSpeechRecognizer(manager);
        // The speaker models ship in the same package; captions stay label-free
        // until the user turns the feature on. The official package is checked
        // against its exact release sizes, so a truncated model is never read as
        // ready and the downloaded copy can take over.
        var speakers = new LocalSpeakerIdentifier(
            new LocalSpeechModelManager(LocalSpeechModelCatalog.Speaker, validateBundledExactSizes: true));
        return new LocalSpeechService(manager, recognizer, speakers);
    }
}
