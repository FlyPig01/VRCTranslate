using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Settings;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Application.Translation;

public interface ITranslationRouteResolver
{
    TranslationRoute Resolve(TextTranslationSource source);
}

/// <summary>Builds the one runtime route used by manual text and speech text.</summary>
public sealed class SettingsTranslationRouteResolver : ITranslationRouteResolver
{
    private static readonly TranslationProfile BuiltInDefaultProfile = new(
        "default-profile",
        "本地测试",
        "echo",
        "echo",
        new Uri("https://localhost/echo"),
        "本地配置");

    private readonly WorkspaceSettings _settings;
    private readonly ITranslationRouteStore _routeStore;

    public SettingsTranslationRouteResolver(WorkspaceSettings settings, ITranslationRouteStore routeStore)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _routeStore = routeStore ?? throw new ArgumentNullException(nameof(routeStore));
    }

    public TranslationRoute Resolve(TextTranslationSource source)
    {
        _ = source;
        var current = _routeStore.GetCurrent();
        if (current is not null)
        {
            return current;
        }

        var translationSettings = _settings.Translation
            ?? throw new InvalidOperationException("Translation settings are not configured.");
        var routeSettings = SelectDefaultRouteSettings(translationSettings);
        var profiles = translationSettings.Profiles ?? [];
        var profile = profiles.FirstOrDefault(
            item => item is not null && string.Equals(item.ProfileId, routeSettings.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null && string.Equals(routeSettings.ProfileId, BuiltInDefaultProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            // A fresh installation has no user profiles yet. Keep the first
            // route usable through the deterministic local provider so the
            // user can open the app and configure a remote service later.
            profile = BuiltInDefaultProfile;
        }

        if (profile is null)
        {
            throw new InvalidOperationException($"Translation profile '{routeSettings.ProfileId}' is not configured.");
        }

        var policy = CreateLanguagePolicy(routeSettings);
        var route = new TranslationRoute(
            "default",
            "默认翻译方案",
            profile,
            policy,
            retryPolicy: new TranslationRetryPolicy(
                routeSettings.MaxRetries,
                TimeSpan.FromSeconds(routeSettings.TimeoutSeconds)));
        _routeStore.SetCurrent(route);
        return route;
    }

    private static TranslationRouteSettings SelectDefaultRouteSettings(TranslationSettings translation)
    {
        var profiles = translation.Profiles ?? [];
        var preferred = translation.DefaultRoute;
        if (preferred is not null && profiles.Any(
                profile => profile is not null && string.Equals(profile.ProfileId, preferred.ProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            return preferred;
        }

        // V1 importer writes SelfRoute. Consume it once without restoring per-feature routes.
        return translation.SelfRoute ?? new TranslationRouteSettings();
    }

    private static LanguagePolicy CreateLanguagePolicy(TranslationRouteSettings settings)
    {
        var source = string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "auto" : settings.SourceLanguage.Trim();
        var target = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "zh-CN" : settings.TargetLanguage.Trim();
        return source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? new LanguagePolicy(SourceLanguageMode.AutoDetect, target)
            : source.Equals("follow_recognizer", StringComparison.OrdinalIgnoreCase)
                ? new LanguagePolicy(SourceLanguageMode.FollowRecognizer, target)
                : new LanguagePolicy(SourceLanguageMode.Fixed, target, source);
    }
}
