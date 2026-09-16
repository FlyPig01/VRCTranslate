using System.Text.Json;
using VrcTranslate.Core.Settings;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Infrastructure.Configuration;

public enum LegacyConfigImportStatus
{
    Imported,
    Missing
}

public sealed record LegacyConfigImportResult(
    WorkspaceSettings Settings,
    IReadOnlyList<string> ImportedSections,
    string SourcePath)
{
    public LegacyConfigImportStatus Status { get; init; } = LegacyConfigImportStatus.Imported;
    public int? LegacyVersion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> CredentialReferences { get; init; } = [];
}

public sealed class LegacyConfigImportException : Exception
{
    public LegacyConfigImportException(string path, string message, Exception? innerException = null)
        : base($"Legacy configuration '{path}' is invalid: {message}", innerException)
    {
        SourcePath = path;
    }

    public string SourcePath { get; }
}

public sealed record LegacyCredential(
    string ProfileId,
    string Provider,
    IReadOnlyDictionary<string, string> Values);

/// <summary>Stores legacy secrets and returns a reference suitable for V2 JSON.</summary>
public interface ILegacyCredentialStore
{
    string Store(LegacyCredential credential);
}

/// <summary>Safe default for previews; it never persists a legacy secret.</summary>
public sealed class ReferenceOnlyLegacyCredentialStore : ILegacyCredentialStore
{
    public string Store(LegacyCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return $"legacy:{credential.ProfileId}";
    }
}

/// <summary>Reads the V1 JSON shape once and maps user-facing settings into V2.</summary>
public sealed class LegacyConfigImporter
{
    private const int MaximumSupportedVersion = 13;
    private static readonly Uri DefaultEndpoint = new("https://localhost/echo");
    private readonly ILegacyCredentialStore _credentialStore;

    public LegacyConfigImporter(ILegacyCredentialStore? credentialStore = null)
    {
        _credentialStore = credentialStore ?? new ReferenceOnlyLegacyCredentialStore();
    }

    public async Task<LegacyConfigImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new LegacyConfigImportResult(new WorkspaceSettings(), [], fullPath)
            {
                Status = LegacyConfigImportStatus.Missing,
                Warnings = ["未找到旧版 config.json，已保留默认设置。"]
            };
        }

        JsonDocument document;
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LegacyConfigImportException(fullPath, "JSON 格式损坏。", exception);
        }
        catch (IOException exception)
        {
            throw new LegacyConfigImportException(fullPath, "文件无法读取。", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LegacyConfigImportException(fullPath, "根节点必须是 JSON 对象。");
            }

            var version = ReadVersion(root, fullPath, out var versionWarning);
            var settings = new WorkspaceSettings { Version = 1 };
            var importedSections = new List<string>();
            var warnings = new List<string>();
            if (versionWarning is not null) warnings.Add(versionWarning);
            var credentialReferences = new List<string>();

            ImportTranslation(root, settings, importedSections, warnings, credentialReferences);
            ImportOsc(root, settings, importedSections, warnings);
            ImportVoice(root, settings, importedSections, warnings, credentialReferences);
            ImportSelfVoice(root, settings, importedSections);
            ImportGlossary(root, settings, importedSections);
            ImportSystem(root, settings, importedSections);

            return new LegacyConfigImportResult(settings, importedSections, fullPath)
            {
                LegacyVersion = version,
                Warnings = warnings,
                CredentialReferences = credentialReferences
            };
        }
    }

    private void ImportTranslation(JsonElement root, WorkspaceSettings settings, List<string> sections, List<string> warnings, List<string> credentialReferences)
    {
        if (!TryObject(root, "translation", out var translation)) return;
        var profiles = ReadProfiles(translation);
        if (profiles.Count == 0) profiles.Add(translation);
        var importedProfiles = new List<TranslationProfile>();
        for (var index = 0; index < profiles.Count; index++)
        {
            var raw = profiles[index];
            var provider = String(raw, "provider", "test");
            var profileId = String(raw, "id", index == 0 ? "legacy-translation" : $"legacy-translation-{index + 1}");
            if (string.IsNullOrWhiteSpace(profileId)) profileId = $"legacy-translation-{index + 1}";
            var values = CredentialValues(raw, provider);
            var options = StringDictionary(raw, "options");
            foreach (var key in TranslationCredentialKeys(provider)) options.Remove(key);
            var reference = values.Count == 0
                ? "legacy:none"
                : _credentialStore.Store(new LegacyCredential(profileId, provider, values));
            if (values.Count > 0)
            {
                credentialReferences.Add(reference);
                if (reference.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"翻译档案“{profileId}”的凭据只生成了引用，需在当前版本中重新验证。");
            }

            importedProfiles.Add(new TranslationProfile(
                profileId,
                String(raw, "name", provider),
                provider,
                String(raw, "model", provider == "test" ? "echo" : "default"),
                UriOrDefault(String(raw, "base_url", "")),
                reference,
                region: String(raw, "region", ""),
                options: options));
        }

        settings.Translation.Profiles = importedProfiles;
        var route = settings.Translation.SelfRoute;
        route.ProfileId = importedProfiles[0].ProfileId;
        var sourceFallback = route.SourceLanguage;
        var targetFallback = route.TargetLanguage;
        if (TryObject(root, "ui", out var ui))
        {
            sourceFallback = String(ui, "source_language", sourceFallback);
            targetFallback = String(ui, "target_language", targetFallback);
        }
        route.SourceLanguage = String(translation, "source_language", sourceFallback);
        route.TargetLanguage = String(translation, "target_language", targetFallback);
        route.MessageFormat = String(translation, "message_format", route.MessageFormat);
        settings.Translation.DefaultRoute.ProfileId = route.ProfileId;
        settings.Translation.DefaultRoute.SourceLanguage = route.SourceLanguage;
        settings.Translation.DefaultRoute.TargetLanguage = route.TargetLanguage;
        settings.Translation.DefaultRoute.MessageFormat = route.MessageFormat;
        settings.Translation.VoiceRoute.ProfileId = route.ProfileId;
        settings.Translation.VoiceRoute.SourceLanguage = route.SourceLanguage;
        settings.Translation.VoiceRoute.TargetLanguage = route.TargetLanguage;
        sections.Add("translation");
    }

    private static List<JsonElement> ReadProfiles(JsonElement translation)
    {
        var profiles = new List<JsonElement>();
        if (!translation.TryGetProperty("profiles", out var value) || value.ValueKind != JsonValueKind.Array) return profiles;
        foreach (var profile in value.EnumerateArray())
            if (profile.ValueKind == JsonValueKind.Object) profiles.Add(profile);
        return profiles;
    }

    private static Dictionary<string, string> CredentialValues(JsonElement raw, string provider)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = provider switch
        {
            "tencent" => new[] { "secret_id", "secret_key" },
            "aliyun" => new[] { "access_key_id", "access_key_secret" },
            _ => new[] { "api_key" }
        };
        foreach (var key in keys)
        {
            var value = String(raw, key, "");
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        }
        return values;
    }

    private static string[] TranslationCredentialKeys(string provider) => provider switch
    {
        "tencent" => ["secret_id", "secret_key"],
        "aliyun" => ["access_key_id", "access_key_secret"],
        _ => ["api_key"]
    };

    private static void ImportOsc(JsonElement root, WorkspaceSettings settings, List<string> sections, List<string> warnings)
    {
        if (!TryObject(root, "osc", out var osc)) return;
        settings.Osc.Host = String(osc, "host", settings.Osc.Host);
        settings.Osc.Port = Integer(osc, "port", settings.Osc.Port, warnings, "osc.port");
        settings.Osc.MinimumIntervalSeconds = Number(osc, "min_interval_seconds", settings.Osc.MinimumIntervalSeconds);
        settings.Osc.PlaySound = Boolean(osc, "play_sound", settings.Osc.PlaySound);
        settings.Osc.ChatboxMaxUnits = Integer(osc, "chatbox_max_units", settings.Osc.ChatboxMaxUnits, warnings, "osc.chatbox_max_units");
        sections.Add("osc");
    }

    private void ImportVoice(
        JsonElement root,
        WorkspaceSettings settings,
        List<string> sections,
        List<string> warnings,
        List<string> credentialReferences)
    {
        if (!TryObject(root, "voice", out var voice)) return;
        settings.Voice.TargetProcessName = String(voice, "target_process_name", settings.Voice.TargetProcessName);
        settings.Voice.TargetWindowTitle = String(voice, "target_window_title", settings.Voice.TargetWindowTitle);
        settings.Voice.OverlayEnabled = Boolean(voice, "overlay_enabled", settings.Voice.OverlayEnabled);
        settings.Voice.OverlayOpacity = Number(voice, "overlay_opacity", settings.Voice.OverlayOpacity);
        settings.Voice.OverlayFontSize = Integer(voice, "font_size", settings.Voice.OverlayFontSize, warnings, "voice.font_size");
        settings.Voice.OverlayMaxItems = Integer(voice, "max_items", settings.Voice.OverlayMaxItems, warnings, "voice.max_items");
        settings.Voice.AsrProfileId = String(voice, "asr_profile_id", settings.Voice.AsrProfileId);
        if (voice.TryGetProperty("asr_profiles", out var rawProfiles) && rawProfiles.ValueKind == JsonValueKind.Array)
        {
            settings.Voice.AsrProfiles = [];
            var index = 0;
            foreach (var raw in rawProfiles.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object) continue;
                var profileId = String(raw, "id", $"legacy-speech-{++index}");
                if (string.IsNullOrWhiteSpace(profileId)) profileId = $"legacy-speech-{index}";
                var provider = String(raw, "provider", "local_offline");
                var options = StringDictionary(raw, "options");
                var values = SpeechCredentialValues(raw, provider, options);
                foreach (var key in SpeechCredentialKeys(provider)) options.Remove(key);
                var reference = values.Count == 0
                    ? (provider == "local_offline" ? "本地模型" : "legacy:none")
                    : _credentialStore.Store(new LegacyCredential(profileId, provider, values));
                if (values.Count > 0)
                {
                    credentialReferences.Add(reference);
                    if (reference.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"语音识别档案“{profileId}”的凭据只生成了引用，需在当前版本中重新验证。");
                }

                settings.Voice.AsrProfiles.Add(new SpeechRecognitionProfileSettings
                {
                    Id = profileId,
                    Name = String(raw, "name", profileId),
                    Provider = provider,
                    Model = String(raw, "model", ""),
                    CredentialReference = reference,
                    Options = options
                });
            }
        }
        if (settings.Voice.AsrProfiles.Count > 0 &&
            !settings.Voice.AsrProfiles.Any(profile => profile.Id.Equals(settings.Voice.AsrProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            settings.Voice.AsrProfileId = settings.Voice.AsrProfiles[0].Id;
        }

        if (voice.TryGetProperty("segment", out var segment) && segment.ValueKind == JsonValueKind.Object)
        {
            settings.Voice.EnergyThreshold = Integer(segment, "energy_threshold", settings.Voice.EnergyThreshold, warnings, "voice.segment.energy_threshold");
            settings.Voice.SilenceMilliseconds = Integer(segment, "silence_ms", settings.Voice.SilenceMilliseconds, warnings, "voice.segment.silence_ms");
            settings.Voice.MinimumSpeechMilliseconds = Integer(segment, "minimum_speech_ms", settings.Voice.MinimumSpeechMilliseconds, warnings, "voice.segment.minimum_speech_ms");
        }
        if (voice.TryGetProperty("overlay", out var overlay) && overlay.ValueKind == JsonValueKind.Object)
        {
            settings.Voice.ShowOriginal = Boolean(overlay, "show_original", settings.Voice.ShowOriginal);
            settings.Voice.OverlayDisplayMode = String(overlay, "display_mode", settings.Voice.OverlayDisplayMode);
            settings.Voice.OverlayEnabled = Boolean(overlay, "enabled", settings.Voice.OverlayEnabled);
            settings.Voice.OverlayOpacity = Number(overlay, "opacity", settings.Voice.OverlayOpacity);
            settings.Voice.OverlayFontSize = Integer(overlay, "font_size", settings.Voice.OverlayFontSize, warnings, "voice.overlay.font_size");
            settings.Voice.OverlayMaxItems = Integer(overlay, "max_items", settings.Voice.OverlayMaxItems, warnings, "voice.overlay.max_items");
        }
        sections.Add("voice");
    }

    private static void ImportSelfVoice(JsonElement root, WorkspaceSettings settings, List<string> sections)
    {
        if (!TryObject(root, "self_voice", out var voice)) return;
        settings.SelfVoice.Enabled = Boolean(voice, "enabled", settings.SelfVoice.Enabled);
        settings.SelfVoice.MicrophoneId = String(voice, "microphone_id", settings.SelfVoice.MicrophoneId);
        settings.SelfVoice.SourceLanguage = String(voice, "source_language", settings.SelfVoice.SourceLanguage);
        settings.SelfVoice.ActivationScope = String(voice, "activation_scope", settings.SelfVoice.ActivationScope);
        settings.SelfVoice.ToggleHotkey = String(voice, "toggle_hotkey", settings.SelfVoice.ToggleHotkey);
        sections.Add("self_voice");
    }

    private static void ImportGlossary(JsonElement root, WorkspaceSettings settings, List<string> sections)
    {
        if (!TryObject(root, "glossary", out var glossary)) return;
        settings.Glossary.Enabled = Boolean(glossary, "enabled", settings.Glossary.Enabled);
        settings.Glossary.BuiltInEnabled = Boolean(glossary, "builtin_enabled", settings.Glossary.BuiltInEnabled);
        sections.Add("glossary");
    }

    private static void ImportSystem(JsonElement root, WorkspaceSettings settings, List<string> sections)
    {
        if (!TryObject(root, "ui", out var ui)) return;
        settings.System.Language = String(ui, "language", settings.System.Language);
        settings.System.QuickInputHotkey = String(ui, "quick_input_hotkey", settings.System.QuickInputHotkey);
        settings.System.VoiceHotkey = String(ui, "voice_hotkey", settings.System.VoiceHotkey);
        sections.Add("ui");
    }

    private static Dictionary<string, string> StringDictionary(JsonElement parent, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            else if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                result[property.Name] = property.Value.ToString();
        }
        return result;
    }

    private static Dictionary<string, string> SpeechCredentialValues(
        JsonElement raw,
        string provider,
        IReadOnlyDictionary<string, string> options)
    {
        var keys = SpeechCredentialKeys(provider);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var value = options.TryGetValue(key, out var optionValue)
                ? optionValue
                : String(raw, key, "");
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        }
        return values;
    }

    private static string[] SpeechCredentialKeys(string provider) => provider switch
    {
        "tencent_realtime" => ["app_id", "secret_id"],
        "aliyun_nls_realtime" => ["app_key", "access_key_id", "access_key_secret"],
        _ => ["api_key"]
    };

    private static int? ReadVersion(JsonElement root, string path, out string? warning)
    {
        warning = null;
        if (!root.TryGetProperty("version", out var value))
        {
            warning = "旧版配置没有 version 字段，按最早版本兼容导入。";
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version) || version < 1 || version > MaximumSupportedVersion)
            throw new LegacyConfigImportException(path, $"version 必须是 1 到 {MaximumSupportedVersion} 的整数。");
        return version;
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value) => parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    private static string String(JsonElement parent, string name, string fallback) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int Integer(JsonElement parent, string name, int fallback, List<string> warnings, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return fallback;
        if (value.TryGetInt32(out var result)) return result;
        warnings.Add($"字段 {path} 类型不正确，已使用默认值。");
        return fallback;
    }

    private static double Number(JsonElement parent, string name, double fallback) => parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : fallback;
    private static bool Boolean(JsonElement parent, string name, bool fallback) => parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static Uri UriOrDefault(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : DefaultEndpoint;
}
