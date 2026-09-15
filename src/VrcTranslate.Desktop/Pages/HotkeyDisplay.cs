using System.Text.Json;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// The one place a page turns the persisted global shortcuts into what it
/// shows. It reads them the way <c>MainWindow.ReadHotkeys</c> reads them, so a
/// shortcut the user cleared never reappears as the shipped default: empty
/// means 未设置, and only a missing, unreadable or unsupported value keeps the
/// default that the poller falls back to.
/// </summary>
internal static class HotkeyDisplay
{
    /// <summary>Shown wherever a shortcut has deliberately been cleared.</summary>
    internal const string Unset = "未设置";

    // The accent colour of the shortcut chips, and the neutral colour the
    // settings page already uses for the same placeholder.
    private static readonly Windows.UI.Color SetColor = Microsoft.UI.ColorHelper.FromArgb(255, 0x2D, 0x73, 0xD5);
    private static readonly Windows.UI.Color UnsetColor = Microsoft.UI.ColorHelper.FromArgb(255, 0x5B, 0x6D, 0x82);

    /// <summary>The 打开输入框 gesture, or an empty string when the user cleared it.</summary>
    internal static string ReadQuickInput() => Read(static settings => settings.QuickInputHotkey, HotkeyDefaults.QuickInput);

    /// <summary>The 他人语音 gesture, or an empty string when the user cleared it.</summary>
    internal static string ReadOtherPlayerVoice() => Read(static settings => settings.VoiceHotkey, HotkeyDefaults.OtherPlayerVoice);

    /// <summary>The 自身语音 gesture, or an empty string when the user cleared it.</summary>
    internal static string ReadSelfVoice() => Read(static settings => settings.SelfVoiceHotkey, HotkeyDefaults.SelfVoice);

    /// <summary>
    /// The chip text for one resolved gesture: 未设置 when the user cleared it,
    /// otherwise the keycap spelling the settings page uses so every readout of
    /// one shortcut looks alike.
    /// </summary>
    internal static string Format(string gesture)
    {
        if (gesture.Length == 0) return Unset;
        return string.Join(
            '+',
            gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(FormatKey));
    }

    private static string FormatKey(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => "Ctrl",
        "ALT" or "MENU" => "Alt",
        "SHIFT" => "Shift",
        "WIN" or "WINDOWS" => "Win",
        _ => part.ToUpperInvariant()
    };

    /// <summary>
    /// Paints one compact shortcut chip: the gesture in the accent colour, or
    /// 未设置 in the neutral placeholder colour so a cleared shortcut never
    /// reads as a key that still works. The automation name says the same thing
    /// as the visible text.
    /// </summary>
    internal static void Apply(TextBlock chip, string gesture)
    {
        chip.Text = Format(gesture);
        chip.Foreground = new SolidColorBrush(gesture.Length == 0 ? UnsetColor : SetColor);
        AutomationProperties.SetName(chip, chip.Text);
    }

    private static string Read(Func<SettingsEntry, string?> select, string fallback)
    {
        var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<SettingsEntry>(File.ReadAllText(path));
                if (settings is not null) return HotkeyDefaults.ResolvePersisted(select(settings), fallback);
            }
        }
        catch
        {
            // A missing or half-written settings file keeps the shipped default,
            // exactly like the global poller does.
        }

        return fallback;
    }

    /// <summary>
    /// Mirrors the shape the shell polls. A property absent from the file keeps
    /// its initializer, which is the shipped default.
    /// </summary>
    private sealed class SettingsEntry
    {
        public string QuickInputHotkey { get; set; } = HotkeyDefaults.QuickInput;
        public string VoiceHotkey { get; set; } = HotkeyDefaults.OtherPlayerVoice;
        public string SelfVoiceHotkey { get; set; } = HotkeyDefaults.SelfVoice;
    }
}
