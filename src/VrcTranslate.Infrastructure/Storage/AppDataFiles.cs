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

    /// <summary>Stage-2 voiceprint library; listed so the portable migration carries it too.</summary>
    public const string Speakers = "v2-speakers.json";

    /// <summary>Crash log written next to the settings; kept out of <see cref="All"/> so it is never migrated.</summary>
    public const string StartupErrorLog = "startup-error.log";

    /// <summary>Every settings document the pre-portable user-profile layout may hold.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Route, Profiles, UserSettings, SelfTranslation, VoiceSettings,
        SelfVoiceSettings, OverlayAppearance, OverlayLayout, Glossary, Speakers,
    ];
}