using VrcTranslate.Application.Translation;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Translation;
using VrcTranslate.Infrastructure.Translation;
using VrcTranslate.Infrastructure.Configuration;
using VrcTranslate.Infrastructure.Osc;
using VrcTranslate.Application.Speech;
using VrcTranslate.Application.Settings;
using VrcTranslate.Application.Subtitles;
using VrcTranslate.Infrastructure.Speech;
using VrcTranslate.Infrastructure.Storage;
using ApplicationTranslationService = VrcTranslate.Application.Translation.TranslationService;
using System.Text.Json;

namespace VrcTranslate.Desktop;

/// <summary>Small composition-root state shared by the shell pages.</summary>
public sealed class AppState
{
    private readonly JsonConfigurationStore<RouteSettings> _settingsStore;
    private readonly JsonConfigurationStore<ProfileDocument> _profileStore;
    private readonly string _selfTranslationSettingsPath;
    private readonly Task _restoreTask;
    private readonly object _routeSync = new();
    private int _routeRevision;
    private OscChatboxClient _osc;
    private TranslationTargetSet _selfTranslationTargets;

    public AppState()
    {
        // Local speech is an optional component. Creating the service is cheap
        // and does not load a model until the user installs and starts it.
        LocalSpeech = LocalSpeechServiceFactory.CreateDefault();
        AudioCapture = new WindowsAudioCaptureFactory();
        AudioDevices = new WindowsAudioDeviceEnumerator();
        // Recognition sessions live at application scope: switching pages must
        // not stop an in-flight translation and the shutdown path needs one
        // place to stop them.
        SubtitleVoice = new SubtitleSpeechSession(this);
        SelfVoice = new SelfVoiceSpeechSession(this);
        // 他人语音 → 字幕 is one feature: the shortcut and the voice page drive
        // this single serialized switch instead of toggling the window and the
        // recognition session separately.
        OtherPlayerCaption = new OtherPlayerCaptionToggle(new OtherPlayerCaptionEndpoint(this));
        RouteStore = new InMemoryTranslationRouteStore();
        RouteStore.SetCurrent(CreateDefaultRoute());
        // The shipped adapters come from the shared registry, so "which services
        // this build offers" cannot drift between the shell and its tests. DeepL
        // and Google are no longer part of it; a profile or route that still
        // names them is handled by the fallbacks below.
        var catalog = new TranslationProviderCatalog(TranslationProviderRegistry.CreateShippedProviders());
        TranslationProviderIds = catalog.Ids;
        _profiles = CreateDefaultProfiles();
        Translator = new ApplicationTranslationService(new RoutedTranslationProvider(catalog), new PassThroughInvariantGuard());
        // Portable layout: every document lives in the data folder beside the
        // executable unless the install location is read-only.
        PortableStorage.MigrateLegacyData();
        OverlayAppearance = new OverlayAppearanceService(new JsonOverlayAppearanceStore(
            PortableStorage.GetPath(AppDataFiles.OverlayAppearance)));
        _settingsStore = new JsonConfigurationStore<RouteSettings>(
            PortableStorage.GetPath(AppDataFiles.Route));
        _profileStore = new JsonConfigurationStore<ProfileDocument>(
            PortableStorage.GetPath(AppDataFiles.Profiles));
        _selfTranslationSettingsPath = PortableStorage.GetPath(AppDataFiles.SelfTranslation);
        _selfTranslationTargets = LoadSelfTranslationTargets();
        _osc = CreateOscClientFromUserSettings();
        _restoreTask = RestoreAsync();
    }

    public InMemoryTranslationRouteStore RouteStore { get; }

    public ApplicationTranslationService Translator { get; }

    /// <summary>Shared appearance state used by both game overlay windows.</summary>
    public OverlayAppearanceService OverlayAppearance { get; }

    /// <summary>Application boundary for the bundled local speech model.</summary>
    public LocalSpeechService LocalSpeech { get; }

    /// <summary>Platform audio factory exposed through the application boundary.</summary>
    public IAudioCaptureFactory AudioCapture { get; }

    /// <summary>Recording endpoints offered in the microphone picker.</summary>
    public IAudioDeviceEnumerator AudioDevices { get; }

    /// <summary>Other-player caption recognition; owns the loopback session across page navigation.</summary>
    public SubtitleSpeechSession SubtitleVoice { get; }

    /// <summary>Own-voice recognition; owns the microphone session across page navigation.</summary>
    public SelfVoiceSpeechSession SelfVoice { get; }

    /// <summary>Master switch for other-player captions: recognition and its caption surface.</summary>
    public OtherPlayerCaptionToggle OtherPlayerCaption { get; }

    /// <summary>Stops every recognition session; called once when the main window closes.</summary>
    public async Task ShutdownSpeechAsync()
    {
        try
        {
            await SubtitleVoice.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await SelfVoice.StopAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyCollection<string> TranslationProviderIds { get; }

    private readonly List<TranslationProfileRecord> _profiles;

    public IReadOnlyList<TranslationProfileRecord> TranslationProfiles => _profiles;

    public TranslationProfileRecord? DefaultProfile =>
        _profiles.FirstOrDefault(profile => profile.Id == CurrentRoute.Profile.ProfileId) ?? _profiles.FirstOrDefault();

    /// <summary>Completes after the optional local route has been restored.</summary>
    public Task Ready => _restoreTask;

    public OscChatboxClient Osc => _osc;

    public void ReloadOscSettings() => _osc = CreateOscClientFromUserSettings();

    /// <summary>
    /// Resolves the folder that holds user state: the data folder beside the
    /// executable, or VRC_TRANSLATE_DATA_DIR when a test or portable launch
    /// overrides it.
    /// </summary>
    public static string ResolveDataDirectory() => PortableStorage.DataDirectory;

    private static OscChatboxClient CreateOscClientFromUserSettings()
    {
        var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<UserOscSettings>(File.ReadAllText(path));
                var host = string.IsNullOrWhiteSpace(settings?.OscHost) ? "127.0.0.1" : settings.OscHost.Trim();
                var port = settings?.OscPort is >= 1 and <= 65535 ? settings.OscPort : 9000;
                return new OscChatboxClient(host, port);
            }
        }
        catch
        {
            // Fall back to the VRChat default when a partial settings file is edited.
        }
        return new OscChatboxClient();
    }

    private sealed class UserOscSettings
    {
        public string OscHost { get; set; } = "127.0.0.1";
        public int OscPort { get; set; } = 9000;
    }

    public event EventHandler? RouteChanged;
    public event EventHandler? TranslationPreviewChanged;
    public event EventHandler? SelfTranslationTargetsChanged;
    public string LastOriginalText { get; private set; } = string.Empty;
    public string LastTranslatedText { get; private set; } = string.Empty;
    public string LastSecondaryTranslatedText { get; private set; } = string.Empty;
    public string? LastSecondaryTargetLanguage { get; private set; }
    public string LastPrimaryTargetLanguage { get; private set; } = "en-US";

    /// <summary>Output languages used by the user's own input and voice.</summary>
    public TranslationTargetSet SelfTranslationTargets => _selfTranslationTargets;

    public void SetTranslationPreview(string original, string translated)
        => SetTranslationPreview(original, translated, null, null);

    public void SetTranslationPreview(
        string original,
        string translated,
        string? secondaryTranslated,
        TranslationTargetSet? targets)
    {
        LastOriginalText = original ?? string.Empty;
        LastTranslatedText = translated ?? string.Empty;
        LastSecondaryTranslatedText = secondaryTranslated ?? string.Empty;
        LastPrimaryTargetLanguage = targets?.PrimaryLanguage ?? CurrentRoute.LanguagePolicy.TargetLanguage;
        LastSecondaryTargetLanguage = targets?.SecondaryLanguage;
        TranslationPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelfTranslationTargets(string primaryLanguage, string? secondaryLanguage = null)
    {
        var targets = new TranslationTargetSet(primaryLanguage, secondaryLanguage);
        _selfTranslationTargets = targets;
        PersistSelfTranslationTargets();
        SelfTranslationTargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TextTranslationBatchResult> TranslateSelfAsync(
        string text,
        TextTranslationSource source = TextTranslationSource.ManualText,
        CancellationToken cancellationToken = default)
    {
        await Ready.ConfigureAwait(false);
        var request = new TextTranslationRequest(
            text,
            CurrentRoute,
            source,
            sourceLanguageHint: "zh-CN");
        return await Translator.TranslateManyAsync(request, SelfTranslationTargets, cancellationToken).ConfigureAwait(false);
    }

    public TranslationRoute CurrentRoute => RouteStore.GetCurrent()!;

    public void SetRoute(TranslationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var provider = NormalizeProvider(route.Profile.Provider);
        if (provider is null || !TranslationProviderIds.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Translation provider '{route.Profile.Provider}' is not registered.", nameof(route));
        }

        if (!IsHttpEndpoint(route.Profile.Endpoint))
        {
            throw new ArgumentException("Translation service endpoint must use http or https.", nameof(route));
        }

        // Keep aliases accepted by imported configurations from leaking into
        // the runtime route, where the provider catalog uses stable IDs.
        if (!string.Equals(provider, route.Profile.Provider, StringComparison.Ordinal))
        {
            route = CloneWithProvider(route, provider);
        }

        lock (_routeSync)
        {
            _routeRevision++;
            RouteStore.SetCurrent(route);
        }
        _ = PersistSafelyAsync(route);
        RouteChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile(TranslationProfileRecord profile, bool setAsDefault)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!TranslationProviderIds.Contains(profile.Provider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Translation provider '{profile.Provider}' is not registered.", nameof(profile));
        if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || !IsHttpEndpoint(endpoint))
            throw new ArgumentException("Translation service endpoint must use http or https.", nameof(profile));

        var normalized = profile with
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? "未命名服务" : profile.DisplayName.Trim(),
            Provider = NormalizeProvider(profile.Provider) ?? profile.Provider.Trim().ToLowerInvariant(),
            Model = string.IsNullOrWhiteSpace(profile.Model) ? DefaultModelForProvider(profile.Provider) : profile.Model.Trim(),
            Endpoint = endpoint.ToString(),
            CredentialReference = string.IsNullOrWhiteSpace(profile.CredentialReference) ? "本地配置" : profile.CredentialReference.Trim()
        };
        var index = _profiles.FindIndex(item => string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _profiles[index] = normalized;
        else _profiles.Add(normalized);
        PersistProfiles();
        if (setAsDefault) SetDefaultProfile(normalized.Id);
    }

    public bool DeleteProfile(string profileId)
    {
        if (_profiles.Count <= 1) return false;
        var index = _profiles.FindIndex(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        var wasDefault = string.Equals(_profiles[index].Id, CurrentRoute.Profile.ProfileId, StringComparison.OrdinalIgnoreCase);
        _profiles.RemoveAt(index);
        PersistProfiles();
        if (wasDefault) SetDefaultProfile(_profiles[0].Id);
        return true;
    }

    public bool SetDefaultProfile(string profileId)
    {
        var profile = _profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !TryCreateRouteFromProfile(profile, out var route) || route is null) return false;
        try
        {
            SetRoute(route);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Route used when the saved one names a provider this build no longer ships
    /// - or was never configured: the first profile that can still be served, so
    /// an old DeepL / Google selection degrades to whatever service the user has
    /// left instead of leaving the shell without a working route. A build whose
    /// only profile is the offline test service uses that one.
    /// </summary>
    private TranslationRoute CreateFallbackRoute() =>
        TranslationProfileResolver.SelectFallback(_profiles, TranslationProviderIds) is { } profile &&
        TryCreateRouteFromProfile(profile, out var route) &&
        route is not null
            ? route
            : CreateDefaultRoute();

    private static bool TryCreateRouteFromProfile(TranslationProfileRecord profile, out TranslationRoute? route)
    {
        route = null;
        if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint)) return false;
        var provider = NormalizeProvider(profile.Provider) ?? profile.Provider.Trim();
        var sourceMode = string.IsNullOrWhiteSpace(profile.SourceLanguage) || profile.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SourceLanguageMode.AutoDetect : SourceLanguageMode.Fixed;
        route = new TranslationRoute("default", profile.DisplayName,
            new TranslationProfile(profile.Id, profile.DisplayName, provider, profile.Model, endpoint,
                profile.CredentialReference, options: profile.Options, region: profile.Region),
            new LanguagePolicy(sourceMode, profile.TargetLanguage, sourceMode == SourceLanguageMode.Fixed ? profile.SourceLanguage : null));
        return true;
    }

    private async Task RestoreAsync()
    {
        try
        {
            var profileDocument = await _profileStore.LoadAsync();
            RestoreProfiles(profileDocument);
            var settings = await _settingsStore.LoadAsync();
            // A save made while the file is loading is newer than the on-disk
            // snapshot and must remain the active route.
            if (!TryBuildRoute(settings, out var route) || route is null || !IsConfiguredRoute(route))
            {
                var fallback = CreateFallbackRoute();
                lock (_routeSync)
                {
                    if (_routeRevision != 0) return;
                    RouteStore.SetCurrent(fallback);
                }
                _ = PersistSafelyAsync(fallback);
                RouteChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            lock (_routeSync)
            {
                // A save may have raced with the file read. Keep the newer
                // in-memory route instead of applying the stale snapshot.
                if (_routeRevision != 0)
                {
                    return;
                }

                RouteStore.SetCurrent(route!);
            }
            RouteChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A malformed local settings file should not prevent the shell from opening.
        }
    }

    private static bool IsConfiguredRoute(TranslationRoute route)
    {
        var profile = route.Profile;
        if (profile.Provider.Equals("echo", StringComparison.OrdinalIgnoreCase)) return true;

        var placeholder = CreateProviderPlaceholders().FirstOrDefault(item =>
            item.Provider.Equals(profile.Provider, StringComparison.OrdinalIgnoreCase));
        if (placeholder is null) return true;

        // Route settings use a synthetic profile id and a generic display name,
        // so only technical values determine whether an old placeholder was
        // actually configured.
        return !string.Equals(profile.Model, placeholder.Model, StringComparison.Ordinal) ||
               !string.Equals(profile.Endpoint.ToString().TrimEnd('/'), placeholder.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(profile.CredentialReference, "本地配置", StringComparison.Ordinal) ||
               !string.Equals(profile.Region, placeholder.Region, StringComparison.OrdinalIgnoreCase) ||
               profile.Options is { Count: > 0 } ||
               !string.Equals(route.LanguagePolicy.TargetLanguage, placeholder.TargetLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PersistSafelyAsync(TranslationRoute route)
    {
        try
        {
            await _settingsStore.SaveAsync(new RouteSettings
            {
                DisplayName = route.DisplayName,
                Provider = route.Profile.Provider,
                Model = route.Profile.Model,
                Endpoint = route.Profile.Endpoint.ToString(),
                CredentialReference = route.Profile.CredentialReference,
                Region = route.Profile.Region,
                Options = route.Profile.Options.ToDictionary(item => item.Key, item => item.Value),
                SourceLanguage = route.LanguagePolicy.SourceMode switch
                {
                    SourceLanguageMode.AutoDetect => "auto",
                    SourceLanguageMode.FollowRecognizer => "follow_recognizer",
                    _ => route.LanguagePolicy.SourceLanguage ?? "auto"
                },
                TargetLanguage = route.LanguagePolicy.TargetLanguage
            });
        }
        catch
        {
            // Persistence is best effort. A read-only profile must not crash
            // the page after the in-memory route has already been applied.
        }
    }

    private void PersistProfiles() => _ = PersistProfilesAsync();

    private async Task PersistProfilesAsync()
    {
        try { await _profileStore.SaveAsync(new ProfileDocument { Profiles = _profiles.ToList() }); }
        catch { }
    }

    private void RestoreProfiles(ProfileDocument document)
    {
        if (document?.Profiles is null || document.Profiles.Count == 0) return;
        // A profile saved while DeepL or Google were still shipped cannot be
        // served any more, so it is dropped here instead of appearing as a
        // broken card; every other saved profile is kept exactly as it was.
        var restored = TranslationProfileResolver
            .RetainServable(document.Profiles, TranslationProviderIds)
            .Select(profile => profile with
            {
                Model = string.IsNullOrWhiteSpace(profile.Model) ? DefaultModelForProvider(profile.Provider) : profile.Model.Trim(),
                Region = profile.Region ?? string.Empty,
                Options = profile.Options ?? new Dictionary<string, string>()
            })
            .ToList();
        // Older builds wrote every provider placeholder to the profile file.
        // Keep only profiles the user actually configured, while preserving
        // custom IDs and all concrete values from real saved profiles.
        var configured = restored.Where(IsConfiguredProfile).ToList();
        if (configured.Count > 0)
        {
            // The catalog is used by the editor, not rendered as a list of
            // ready-to-use services. The list starts with the local test
            // profile and then adds only saved configurations.
            var merged = CreateDefaultProfiles()
                .ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var profile in configured) merged[profile.Id] = profile;
            _profiles.Clear();
            _profiles.AddRange(merged.Values);
        }

        // Rewrite a legacy file once so hidden placeholders do not reappear
        // on the next launch. A failed cleanup is harmless; the in-memory list
        // is already filtered for the current session.
        if (configured.Count != restored.Count)
            PersistProfiles();
    }

    private static List<TranslationProfileRecord> CreateDefaultProfiles() =>
    [
        // Only this offline profile is available on a fresh installation.
        // Other providers are offered by the add-service dialog and become
        // visible after the user saves an actual configuration.
        new("local-test", "本地测试", "echo", "echo", "https://localhost/echo", "本地配置", "auto", "zh-CN", "", new Dictionary<string, string>())
    ];

    private static List<TranslationProfileRecord> CreateProviderPlaceholders() =>
    [
        new("deepseek", "DeepSeek", "deepseek", "deepseek-flash", "https://api.deepseek.com", "本地配置", "auto", "zh-CN", "", new Dictionary<string, string>()),
        new("xiaomi", "小米 MiMo", "xiaomi", "mimo-v2.5", "https://api.xiaomimimo.com/v1", "本地配置", "auto", "zh-CN", "", new Dictionary<string, string>()),
        new("tencent", "腾讯云翻译", "tencent", "TextTranslate", "https://tmt.tencentcloudapi.com", "本地配置", "auto", "zh-CN", "ap-beijing", new Dictionary<string, string>()),
        new("aliyun", "阿里云机器翻译", "aliyun", "general", "https://mt.cn-hangzhou.aliyuncs.com", "本地配置", "auto", "zh-CN", "cn-hangzhou", new Dictionary<string, string>())
    ];

    private static bool IsConfiguredProfile(TranslationProfileRecord profile)
    {
        if (profile.Provider.Equals("echo", StringComparison.OrdinalIgnoreCase) ||
            profile.Id.Equals("local-test", StringComparison.OrdinalIgnoreCase))
            return true;

        var placeholder = CreateProviderPlaceholders().FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
        if (placeholder is null)
            return true; // A user-created profile keeps its own identity.

        return !string.Equals(profile.DisplayName, placeholder.DisplayName, StringComparison.Ordinal) ||
               !string.Equals(profile.Provider, placeholder.Provider, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(profile.Model, placeholder.Model, StringComparison.Ordinal) ||
               !string.Equals(profile.Endpoint.TrimEnd('/'), placeholder.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(profile.CredentialReference, "本地配置", StringComparison.Ordinal) ||
               !string.Equals(profile.SourceLanguage, placeholder.SourceLanguage, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(profile.TargetLanguage, placeholder.TargetLanguage, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(profile.Region, placeholder.Region, StringComparison.OrdinalIgnoreCase) ||
               profile.Options is { Count: > 0 };
    }

    private bool TryBuildRoute(RouteSettings settings, out TranslationRoute? route)
    {
        route = null;
        if (settings is null || string.IsNullOrWhiteSpace(settings.Model)) return false;

        var provider = NormalizeProvider(settings.Provider);
        if (provider is null || !TranslationProviderIds.Contains(provider, StringComparer.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || !IsHttpEndpoint(endpoint)) return false;

        var displayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? "默认翻译" : settings.DisplayName.Trim();
        var targetLanguage = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "zh-CN" : settings.TargetLanguage.Trim();
        var source = settings.SourceLanguage?.Trim();
        var sourceMode = string.IsNullOrWhiteSpace(source) || source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SourceLanguageMode.AutoDetect
            : source.Equals("follow_recognizer", StringComparison.OrdinalIgnoreCase)
                ? SourceLanguageMode.FollowRecognizer
                : SourceLanguageMode.Fixed;

        try
        {
            route = new TranslationRoute(
                "default",
                displayName,
                new TranslationProfile(
                    "default-profile",
                    displayName,
                    provider,
                    settings.Model.Trim(),
                    endpoint,
                    string.IsNullOrWhiteSpace(settings.CredentialReference) ? "本地配置" : settings.CredentialReference.Trim(),
                    region: settings.Region,
                    options: settings.Options),
                new LanguagePolicy(sourceMode, targetLanguage, sourceMode == SourceLanguageMode.Fixed ? source : null));
            return true;
        }
        catch (ArgumentException)
        {
            route = null;
            return false;
        }
    }

    private static TranslationRoute CloneWithProvider(TranslationRoute route, string provider) => new(
        route.RouteId,
        route.DisplayName,
        new TranslationProfile(
            route.Profile.ProfileId,
            route.Profile.DisplayName,
            provider,
            route.Profile.Model,
            route.Profile.Endpoint,
            route.Profile.CredentialReference,
            route.Profile.SupportsAutoDetection,
            route.Profile.IsVerified,
            route.Profile.LastVerifiedAt,
            route.Profile.Region,
            route.Profile.Options),
        route.LanguagePolicy,
        route.InvariantPolicy,
        route.RetryPolicy);

    /// <summary>
    /// Stable id for a saved provider spelling. The registry owns the aliases of
    /// earlier builds; the ids of the removed DeepL / Google providers resolve to
    /// nothing here, which is what sends a route down the fallback path instead
    /// of dispatching it to a service this build no longer has.
    /// </summary>
    private static string? NormalizeProvider(string? provider) => TranslationProviderRegistry.TryResolveId(provider);

    private static string DefaultModelForProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "echo" => "本地回显",
        "deepseek" => "deepseek-flash",
        "xiaomi" => "mimo-v2.5",
        "tencent" => "TextTranslate",
        "aliyun" => "general",
        _ => "deepseek-flash"
    };

    private static bool IsHttpEndpoint(Uri endpoint) =>
        endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private TranslationTargetSet LoadSelfTranslationTargets()
    {
        try
        {
            if (File.Exists(_selfTranslationSettingsPath))
            {
                var document = JsonSerializer.Deserialize<SelfTranslationDocument>(
                    File.ReadAllText(_selfTranslationSettingsPath));
                if (document is not null && !string.IsNullOrWhiteSpace(document.PrimaryLanguage))
                {
                    return new TranslationTargetSet(document.PrimaryLanguage, document.SecondaryLanguage);
                }
            }
        }
        catch
        {
            // A malformed optional file should never prevent the shell from opening.
        }

        return new TranslationTargetSet("en-US", "ja-JP");
    }

    private void PersistSelfTranslationTargets()
    {
        try
        {
            var directory = Path.GetDirectoryName(_selfTranslationSettingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(
                _selfTranslationSettingsPath,
                JsonSerializer.Serialize(
                    new SelfTranslationDocument
                    {
                        PrimaryLanguage = SelfTranslationTargets.PrimaryLanguage,
                        SecondaryLanguage = SelfTranslationTargets.SecondaryLanguage
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // UI state remains valid when the optional settings file is read-only.
        }
    }

    private static TranslationRoute CreateDefaultRoute() => new(
        "default",
        "默认翻译",
        new TranslationProfile(
            "default-profile",
            "本地回显服务",
            "echo",
            "echo",
            new Uri("https://localhost/echo"),
            "本地配置"),
        new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN"));

    private sealed class PassThroughInvariantGuard : IInvariantProtectionGuard
    {
        public ProtectedText Protect(string sourceText, InvariantProtectionPolicy policy) => new(sourceText, []);

        public InvariantValidationResult Validate(ProtectedText protectedText, string translatedText) => InvariantValidationResult.Valid;
    }

    private sealed class RouteSettings
    {
        public string DisplayName { get; set; } = "默认翻译";
        public string Provider { get; set; } = "echo";
        public string Model { get; set; } = "echo";
        public string Endpoint { get; set; } = "https://localhost/echo";
        public string CredentialReference { get; set; } = "本地配置";
        public string Region { get; set; } = string.Empty;
        public Dictionary<string, string> Options { get; set; } = [];
        public string SourceLanguage { get; set; } = "auto";
        public string TargetLanguage { get; set; } = "zh-CN";
    }

    private sealed class ProfileDocument
    {
        public List<TranslationProfileRecord> Profiles { get; set; } = [];
    }

    private sealed class SelfTranslationDocument
    {
        public string PrimaryLanguage { get; set; } = "en-US";
        public string? SecondaryLanguage { get; set; } = "ja-JP";
    }
}
