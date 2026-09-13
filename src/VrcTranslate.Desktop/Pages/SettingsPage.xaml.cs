using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private bool _hotkeyConfirmationOpen;
    private UserSettings _savedSettings = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCTranslate", "v2-user-settings.json");

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        Loaded += (_, _) => QueueResponsiveLayout();
        HotkeyGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        OscGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        OscHeaderGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        ContentColumn.Width = Math.Max(0, Math.Min(960, e.NewSize.Width - 64));
        QueueResponsiveLayout();
    }

    private void QueueResponsiveLayout()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            LayoutHotkeys(HotkeyGrid?.ActualWidth ?? 0);
            LayoutOsc(OscGrid?.ActualWidth ?? 0);
            LayoutOscHeader(OscHeaderGrid?.ActualWidth ?? 0);
        });
    }

    private void LayoutOscHeader(double width)
    {
        if (OscHeaderGrid is null || OscHeaderTitle is null || OscHeaderHint is null || width <= 0) return;

        var compact = width < 430;
        OscHeaderGrid.ColumnDefinitions.Clear();
        OscHeaderGrid.RowDefinitions.Clear();
        if (compact)
        {
            OscHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OscHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            OscHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(OscHeaderTitle, 0);
            Grid.SetRow(OscHeaderTitle, 0);
            Grid.SetColumn(OscHeaderHint, 0);
            Grid.SetRow(OscHeaderHint, 1);
            OscHeaderHint.HorizontalAlignment = HorizontalAlignment.Left;
            OscHeaderHint.Margin = new Thickness(41, 0, 0, 0);
            return;
        }

        OscHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        OscHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        OscHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(OscHeaderTitle, 0);
        Grid.SetRow(OscHeaderTitle, 0);
        Grid.SetColumn(OscHeaderHint, 1);
        Grid.SetRow(OscHeaderHint, 0);
        OscHeaderHint.HorizontalAlignment = HorizontalAlignment.Right;
        OscHeaderHint.Margin = new Thickness(0);
    }

    private void LayoutHotkeys(double width)
    {
        if (HotkeyGrid is null || width <= 0) return;

        // Keep the three actions readable on compact windows. The long hint
        // column is useful on desktop, but it is hidden when there is not
        // enough room for a usable shortcut editor.
        var desktop = width >= 860;
        HotkeyGrid.ColumnDefinitions.Clear();
        HotkeyGrid.RowDefinitions.Clear();
        if (desktop)
        {
            HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetHotkeyRow(QuickInputHotkeyLabel, QuickInputHotkeyBox, QuickInputHotkeyHint, 0, true);
            SetHotkeyRow(VoiceHotkeyLabel, VoiceHotkeyBox, VoiceHotkeyHint, 1, true);
            SetHotkeyRow(SelfVoiceHotkeyLabel, SelfVoiceHotkeyBox, SelfVoiceHotkeyHint, 2, true);
            return;
        }

        HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++)
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetHotkeyRow(QuickInputHotkeyLabel, QuickInputHotkeyBox, QuickInputHotkeyHint, 0, false);
        SetHotkeyRow(VoiceHotkeyLabel, VoiceHotkeyBox, VoiceHotkeyHint, 1, false);
        SetHotkeyRow(SelfVoiceHotkeyLabel, SelfVoiceHotkeyBox, SelfVoiceHotkeyHint, 2, false);
    }

    private static void SetHotkeyRow(TextBlock label, TextBox editor, TextBlock hint, int row, bool showHint)
    {
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        Grid.SetRow(hint, row);
        Grid.SetColumn(hint, 2);
        hint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        label.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.MinWidth = 0;
    }

    private void LayoutOsc(double width)
    {
        if (OscGrid is null || width <= 0) return;
        var compact = width < 560;
        OscGrid.ColumnDefinitions.Clear();
        if (compact)
        {
            OscGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(OscHostBox, 0);
            Grid.SetColumn(OscPortBox, 0);
            Grid.SetRow(OscHostBox, 0);
            Grid.SetRow(OscPortBox, 1);
            OscGrid.RowDefinitions.Clear();
            OscGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            OscGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            OscGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            OscGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OscGrid.RowDefinitions.Clear();
            OscGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(OscHostBox, 0);
            Grid.SetColumn(OscPortBox, 1);
            Grid.SetRow(OscHostBox, 0);
            Grid.SetRow(OscPortBox, 0);
        }
        OscHostBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        OscPortBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        OscHostBox.MinWidth = 0;
        OscPortBox.MinWidth = 0;
    }

    private void LoadSettings()
    {
        _loading = true;
        var settings = new UserSettings();
        try
        {
            if (File.Exists(_path))
                settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? settings;
        }
        catch { }
        _savedSettings = settings;
        QuickInputHotkeyBox.Text = settings.QuickInputHotkey;
        VoiceHotkeyBox.Text = settings.VoiceHotkey;
        SelfVoiceHotkeyBox.Text = settings.SelfVoiceHotkey;
        OscHostBox.Text = settings.OscHost;
        OscPortBox.Text = settings.OscPort.ToString();
        _loading = false;
    }

    private async void OnResetHotkeysClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "恢复默认快捷键",
            Content = new TextBlock
            {
                Text = "将三个快捷键恢复为默认值？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _loading = true;
        QuickInputHotkeyBox.Text = "Ctrl+Alt+I";
        VoiceHotkeyBox.Text = "F7";
        SelfVoiceHotkeyBox.Text = "Ctrl+F8";
        _loading = false;
        if (TryReadPendingHotkeys(out var settings, out _))
            SaveSettingsIfValid(settings);
    }

    private void OnSettingTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || IsHotkeyBox(sender)) return;
        SaveSettingsIfValid();
    }

    private async void OnHotkeyBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;
        await ConfirmHotkeyChangeAsync(box);
    }

    private async void OnHotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || sender is not TextBox box) return;
        e.Handled = true;
        await ConfirmHotkeyChangeAsync(box);
    }

    private async Task ConfirmHotkeyChangeAsync(TextBox changedBox)
    {
        if (_hotkeyConfirmationOpen) return;
        var current = changedBox.Text.Trim();
        var saved = GetSavedHotkey(changedBox);
        if (string.Equals(current, saved, StringComparison.Ordinal)) return;

        if (!TryReadPendingHotkeys(out var pending, out var validationError))
        {
            ShowResult(validationError, InfoBarSeverity.Warning);
            RestoreHotkey(changedBox);
            return;
        }

        var actionName = GetHotkeyLabel(changedBox);
        var dialog = new ContentDialog
        {
            Title = "确认快捷键",
            Content = new TextBlock
            {
                Text = $"将“{actionName}”设为 {current}？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        _hotkeyConfirmationOpen = true;
        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                RestoreHotkey(changedBox);
                return;
            }

            if (!SaveSettingsIfValid(pending)) RestoreHotkey(changedBox);
        }
        finally
        {
            _hotkeyConfirmationOpen = false;
        }
    }

    private bool TryReadPendingHotkeys(out UserSettings settings, out string error)
    {
        settings = new UserSettings
        {
            QuickInputHotkey = QuickInputHotkeyBox.Text.Trim(),
            VoiceHotkey = VoiceHotkeyBox.Text.Trim(),
            SelfVoiceHotkey = SelfVoiceHotkeyBox.Text.Trim(),
            OscHost = _savedSettings.OscHost,
            OscPort = _savedSettings.OscPort,
            OscIntervalSeconds = _savedSettings.OscIntervalSeconds,
            PlaySound = _savedSettings.PlaySound,
            KeepRunning = _savedSettings.KeepRunning
        };

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in new[]
        {
            ("打开输入框", settings.QuickInputHotkey),
            ("字幕", settings.VoiceHotkey),
            ("自身语音", settings.SelfVoiceHotkey)
        })
        {
            string value;
            try { value = HotkeyBinding.Normalize(pair.Item2); }
            catch (ArgumentException)
            {
                error = $"“{pair.Item1}”需要填写有效快捷键。";
                return false;
            }

            if (!normalized.TryAdd(value, pair.Item1))
            {
                error = $"“{pair.Item1}”与“{normalized[value]}”使用了相同快捷键。";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private bool SaveSettingsIfValid(UserSettings? hotkeys = null)
    {
        if (_loading) return false;
        if (!int.TryParse(OscPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowResult("端口范围应为 1 到 65535。", InfoBarSeverity.Warning);
            return false;
        }

        var source = hotkeys ?? _savedSettings;
        var settings = new UserSettings
        {
            QuickInputHotkey = source.QuickInputHotkey.Trim(),
            VoiceHotkey = source.VoiceHotkey.Trim(),
            SelfVoiceHotkey = source.SelfVoiceHotkey.Trim(),
            OscHost = string.IsNullOrWhiteSpace(OscHostBox.Text) ? "127.0.0.1" : OscHostBox.Text.Trim(),
            OscPort = port,
            OscIntervalSeconds = source.OscIntervalSeconds,
            PlaySound = source.PlaySound,
            KeepRunning = source.KeepRunning
        };
        if (!TryValidateSavedHotkeys(settings, out var hotkeyError))
        {
            ShowResult(hotkeyError, InfoBarSeverity.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            _savedSettings = settings;
            ((App)global::Microsoft.UI.Xaml.Application.Current).State.ReloadOscSettings();
            SettingsInfo.IsOpen = false;
            return true;
        }
        catch (IOException exception)
        {
            ShowResult($"设置保存失败：{exception.Message}", InfoBarSeverity.Error);
            return false;
        }
    }

    private static bool TryValidateSavedHotkeys(UserSettings settings, out string error)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in new[]
        {
            ("打开输入框", settings.QuickInputHotkey),
            ("字幕", settings.VoiceHotkey),
            ("自身语音", settings.SelfVoiceHotkey)
        })
        {
            string value;
            try { value = HotkeyBinding.Normalize(pair.Item2); }
            catch (ArgumentException)
            {
                error = $"“{pair.Item1}”需要填写有效快捷键。";
                return false;
            }

            if (!normalized.TryAdd(value, pair.Item1))
            {
                error = $"“{pair.Item1}”与“{normalized[value]}”使用了相同快捷键。";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsHotkeyBox(object sender) => sender is TextBox box && box.Name is "QuickInputHotkeyBox" or "VoiceHotkeyBox" or "SelfVoiceHotkeyBox";

    private string GetSavedHotkey(TextBox box) => box == QuickInputHotkeyBox ? _savedSettings.QuickInputHotkey : box == VoiceHotkeyBox ? _savedSettings.VoiceHotkey : _savedSettings.SelfVoiceHotkey;

    private string GetHotkeyLabel(TextBox box) => box == QuickInputHotkeyBox ? "打开输入框" : box == VoiceHotkeyBox ? "字幕" : "自身语音";

    private void RestoreHotkey(TextBox box)
    {
        _loading = true;
        box.Text = GetSavedHotkey(box);
        _loading = false;
    }

    private void ShowResult(string message, InfoBarSeverity severity)
    {
        SettingsInfo.Title = severity == InfoBarSeverity.Success ? "已保存" : "请检查";
        SettingsInfo.Message = message;
        SettingsInfo.Severity = severity;
        SettingsInfo.IsOpen = true;
    }

    private sealed class UserSettings
    {
        public string QuickInputHotkey { get; set; } = "Ctrl+Alt+I";
        public string VoiceHotkey { get; set; } = "F7";
        public string SelfVoiceHotkey { get; set; } = "Ctrl+F8";
        public string OscHost { get; set; } = "127.0.0.1";
        public int OscPort { get; set; } = 9000;
        public double OscIntervalSeconds { get; set; } = 1.5;
        public bool PlaySound { get; set; } = true;
        public bool KeepRunning { get; set; } = true;
    }
}
