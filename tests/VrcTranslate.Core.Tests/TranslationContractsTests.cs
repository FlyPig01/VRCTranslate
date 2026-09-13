using VrcTranslate.Core.Translation;
using VrcTranslate.Core.Settings;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class TranslationContractsTests
{
    [Fact]
    public void FixedLanguagePolicy_requires_source_language()
    {
        Assert.Throws<ArgumentException>(() => new LanguagePolicy(SourceLanguageMode.Fixed, "zh-CN"));
    }

    [Fact]
    public void Route_rejects_auto_detection_when_profile_does_not_support_it()
    {
        var profile = new TranslationProfile(
            "profile-1", "Local", "provider", "model", new Uri("https://example.test"), "credential", false);

        Assert.Throws<ArgumentException>(() => new TranslationRoute(
            "route-1", "Default", profile, new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN")));
    }

    [Fact]
    public void Request_defaults_correlation_id_and_trims_text()
    {
        var route = CreateRoute();
        var request = new TextTranslationRequest("  hello  ", route, TextTranslationSource.ManualText);

        Assert.Equal("hello", request.Text);
        Assert.False(string.IsNullOrWhiteSpace(request.CorrelationId));
    }

    [Fact]
    public void Translation_target_set_supports_an_optional_distinct_second_language()
    {
        var targets = new TranslationTargetSet("en-US", "ja-JP");

        Assert.Equal(["en-US", "ja-JP"], targets.Languages);
        Assert.Throws<ArgumentException>(() => new TranslationTargetSet("en-US", "en-US"));
    }

    [Fact]
    public void Route_can_change_target_without_changing_source_or_profile()
    {
        var route = CreateRoute();
        var japanese = route.ForTargetLanguage("ja-JP");

        Assert.Equal("ja-JP", japanese.LanguagePolicy.TargetLanguage);
        Assert.Equal(route.LanguagePolicy.SourceMode, japanese.LanguagePolicy.SourceMode);
        Assert.Same(route.Profile, japanese.Profile);
    }

    [Fact]
    public void Workspace_defaults_retain_non_translation_features()
    {
        var settings = new WorkspaceSettings();

        Assert.Equal("VRChat.exe", settings.Voice.TargetProcessName);
        Assert.Equal("local-sensevoice", settings.Voice.AsrProfileId);
        Assert.Equal(9000, settings.Osc.Port);
        Assert.Equal("Ctrl+Alt+I", settings.System.QuickInputHotkey);
        Assert.True(settings.Glossary.BuiltInEnabled);
        Assert.False(settings.SelfVoice.Enabled);
    }

    [Fact]
    public void Settings_validator_detects_duplicate_global_hotkeys()
    {
        var settings = new WorkspaceSettings();
        settings.System.VoiceHotkey = settings.System.QuickInputHotkey;

        var result = WorkspaceSettingsValidator.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Path == "System.VoiceHotkey");
    }

    [Theory]
    [InlineData("ctrl + alt + i", "CTRL+ALT+I")]
    [InlineData("alt + control + shift + f12", "CTRL+ALT+SHIFT+F12")]
    [InlineData("windows + j", "WIN+J")]
    public void Hotkey_normalization_canonicalizes_aliases_and_modifier_order(string input, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.Normalize(input));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Ctrl+I")]
    [InlineData("F13")]
    [InlineData("Ctrl+Alt")]
    public void Hotkey_normalization_rejects_keys_the_desktop_poller_cannot_handle(string input)
    {
        Assert.Throws<ArgumentException>(() => HotkeyBinding.Normalize(input));
    }

    [Fact]
    public void Settings_validator_detects_duplicate_hotkeys_after_alias_normalization()
    {
        var settings = new WorkspaceSettings();
        settings.System.VoiceHotkey = "control + menu + i";

        var result = WorkspaceSettingsValidator.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Path == "System.VoiceHotkey");
    }

    private static TranslationRoute CreateRoute() => new(
        "route-1",
        "Default",
        new TranslationProfile("profile-1", "Local", "provider", "model", new Uri("https://example.test"), "credential"),
        new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN"));
}
