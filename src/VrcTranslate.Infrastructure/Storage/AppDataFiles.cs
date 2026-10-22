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

    /// <summary>Tencent Cloud ASR preferences; its secret is DPAPI-encrypted.</summary>
    public const string TencentAsr = "v2-tencent-asr.json";

    /// <summary>Alibaba Cloud Qwen LiveTranslate preferences; its API key is DPAPI-encrypted.</summary>
    public const string QwenAsr = "v2-qwen-asr.json";

    /// <summary>Stage-2 voiceprint library.</summary>
    public const string Speakers = "v2-speakers.json";

    /// <summary>Crash log written next to the settings.</summary>
    public const string StartupErrorLog = "startup-error.log";

    /// <summary>
    /// Bounded, overwritten log of the last process loopback activations. It
    /// holds technical values only (build, formats, HRESULTs).
    /// </summary>
    public const string CaptureDiagnostics = "capture-diagnostics.log";

    /// <summary>
    /// Bounded technical log of the caption path: self-test, audio level,
    /// segments, recognition counts, publish/append results and overlay state.
    /// Never contains recognized or translated text.
    /// </summary>
    public const string CaptionDiagnostics = "caption-diagnostics.log";

}
