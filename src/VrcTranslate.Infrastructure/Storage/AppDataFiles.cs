namespace VrcTranslate.Infrastructure.Storage;

/// <summary>Per-user documents VRCTranslate keeps in its data folder.</summary>
public static class AppDataFiles
{
    public const string Route = "v2-route.json";
    public const string Profiles = "v2-profiles.json";
    public const string UserSettings = "v2-user-settings.json";
    public const string SelfTranslation = "v2-self-translation.json";
    public const string VoiceSettings = "v2-voice-settings.json";
    public const string SelfVoiceSettings = "v2-self-voice-settings.json";
    public const string OverlayAppearance = "v2-overlay-appearance.json";
    public const string OverlayLayout = "v2-overlay-layout.json";
    public const string Glossary = "glossary.json";

    /// <summary>Stage-2 voiceprint library.</summary>
    public const string Speakers = "v2-speakers.json";

    /// <summary>Crash log written next to the settings.</summary>
    public const string StartupErrorLog = "startup-error.log";

    /// <summary>
    /// Bounded, overwritten log of the last process loopback activations. It
    /// holds technical values only (build, formats, HRESULTs).
    /// </summary>
    public const string CaptureDiagnostics = "capture-diagnostics.log";

}
