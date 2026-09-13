using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Settings;
using VrcTranslate.Application.Translation;
using VrcTranslate.Core.Settings;
using VrcTranslate.Core.Translation;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class SettingsUseCaseTests
{
    [Fact]
    public void Resolver_uses_one_cached_route_for_manual_and_speech()
    {
        var profile = new TranslationProfile(
            "legacy", "Legacy", "echo", "echo", new Uri("https://localhost/echo"), "local", true, true, DateTimeOffset.UtcNow);
        var settings = new WorkspaceSettings();
        settings.Translation.Profiles = [profile];
        settings.Translation.SelfRoute.ProfileId = profile.ProfileId;
        var store = new InMemoryTranslationRouteStore();
        var resolver = new SettingsTranslationRouteResolver(settings, store);

        var manualRoute = resolver.Resolve(TextTranslationSource.ManualText);
        var speechRoute = resolver.Resolve(TextTranslationSource.SpeechRecognition);

        Assert.Same(manualRoute, speechRoute);
        Assert.Equal(profile.ProfileId, speechRoute.Profile.ProfileId);
    }

    [Fact]
    public void Resolver_uses_builtin_echo_route_before_profiles_are_configured()
    {
        var resolver = new SettingsTranslationRouteResolver(
            new WorkspaceSettings(),
            new InMemoryTranslationRouteStore());

        var route = resolver.Resolve(TextTranslationSource.ManualText);

        Assert.Equal("default-profile", route.Profile.ProfileId);
        Assert.Equal("echo", route.Profile.Provider);
        Assert.Equal("echo", route.Profile.Model);
        Assert.Equal("zh-CN", route.LanguagePolicy.TargetLanguage);
    }

    [Fact]
    public async Task Hotkey_router_normalizes_gesture_and_dispatches_action()
    {
        var handler = new RecordingHandler();
        var router = new HotkeyActionRouter(new SystemSettings(), handler);

        await router.DispatchAsync(" ctrl + alt + i ");

        Assert.Equal(HotkeyAction.OpenQuickInput, Assert.Single(handler.Actions));
    }

    [Fact]
    public void Hotkey_router_rejects_duplicate_bindings()
    {
        var settings = new SystemSettings { VoiceHotkey = "Ctrl+Alt+I" };

        Assert.Throws<ArgumentException>(() => new HotkeyActionRouter(settings, new RecordingHandler()));
    }

    [Fact]
    public void Hotkey_router_returns_false_for_malformed_gesture()
    {
        var router = new HotkeyActionRouter(new SystemSettings(), new RecordingHandler());

        var resolved = router.TryResolve("+", out _);

        Assert.False(resolved);
    }

    private sealed class RecordingHandler : IHotkeyActionHandler
    {
        public List<HotkeyAction> Actions { get; } = [];

        public Task HandleAsync(HotkeyAction action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
