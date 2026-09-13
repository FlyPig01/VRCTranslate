using VrcTranslate.Core.Translation;

namespace VrcTranslate.Core.Settings;

/// <summary>Purpose-specific route settings retained from the V1 configuration model.</summary>
public sealed class TranslationRouteSettings
{
    public string ProfileId { get; set; } = "default-profile";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string MessageFormat { get; set; } = "translation_only";
    public string OverflowPolicy { get; set; } = "split";
    public double TimeoutSeconds { get; set; } = 8;
    public int MaxRetries { get; set; } = 2;
    public int QueueLimit { get; set; } = 8;
    public double TaskTtlSeconds { get; set; } = 4;
    public string RomajiMode { get; set; } = "auto";
    public bool GlossaryEnabled { get; set; } = true;
}

public sealed class TranslationSettings
{
    public List<TranslationProfile> Profiles { get; set; } = [];
    /// <summary>V2 runtime route shared by manual text and speech text.</summary>
    public TranslationRouteSettings DefaultRoute { get; set; } = new();
    /// <summary>Legacy per-feature routes retained only for one-time migration.</summary>
    public TranslationRouteSettings SelfRoute { get; set; } = new();
    public TranslationRouteSettings VoiceRoute { get; set; } = new() { QueueLimit = 2, TaskTtlSeconds = 20 };
}

public sealed class SpeechRecognitionProfileSettings
{
    public string Id { get; set; } = "local-sensevoice";
    public string Name { get; set; } = "本地 SenseVoice";
    public string Provider { get; set; } = "local_offline";
    public string Model { get; set; } = "SenseVoiceSmall";
    public string CredentialReference { get; set; } = "本地模型";
    public Dictionary<string, string> Options { get; set; } = [];
}

public sealed class VoiceSettings
{
    public string TargetProcessName { get; set; } = "VRChat.exe";
    public string TargetWindowTitle { get; set; } = string.Empty;
    public string AsrProfileId { get; set; } = "local-sensevoice";
    public List<SpeechRecognitionProfileSettings> AsrProfiles { get; set; } = [new()];
    public string VadPreset { get; set; } = "balanced";
    public int EnergyThreshold { get; set; } = 350;
    public int SilenceMilliseconds { get; set; } = 650;
    public int MinimumSpeechMilliseconds { get; set; } = 300;
    public bool OverlayEnabled { get; set; } = true;
    public bool ShowOriginal { get; set; } = true;
    public string OverlayDisplayMode { get; set; } = "both";
    public double OverlayOpacity { get; set; } = 0.9;
    public int OverlayFontSize { get; set; } = 18;
    public int OverlayMaxItems { get; set; } = 3;
}

public sealed class SelfVoiceSettings
{
    public bool Enabled { get; set; }
    public string MicrophoneId { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "zh-CN";
    public string ActivationScope { get; set; } = "vrchat_foreground";
    public string ToggleHotkey { get; set; } = "Ctrl+F8";
}

public sealed class OscSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public double MinimumIntervalSeconds { get; set; } = 1.5;
    public bool PlaySound { get; set; } = true;
    public int ChatboxMaxUnits { get; set; } = 144;
}

public sealed class GlossarySettings
{
    public bool Enabled { get; set; } = true;
    public bool BuiltInEnabled { get; set; } = true;
}

public sealed class SystemSettings
{
    public bool KeepRunningWhenClosed { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public string QuickInputHotkey { get; set; } = "Ctrl+Alt+I";
    public string VoiceHotkey { get; set; } = "F7";
    public string SelfVoiceHotkey { get; set; } = "Ctrl+F8";
}

public sealed class WorkspaceSettings
{
    public int Version { get; set; } = 1;
    public TranslationSettings Translation { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public SelfVoiceSettings SelfVoice { get; set; } = new();
    public OscSettings Osc { get; set; } = new();
    public GlossarySettings Glossary { get; set; } = new();
    public SystemSettings System { get; set; } = new();

    public SettingsValidationResult Validate() => WorkspaceSettingsValidator.Validate(this);
}
