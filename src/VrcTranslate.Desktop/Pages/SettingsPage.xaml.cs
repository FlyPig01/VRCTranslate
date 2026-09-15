using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Storage;
using Windows.System;
using Windows.UI.Core;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private UserSettings _savedSettings = new();
    private readonly string _path = PortableStorage.GetPath(AppDataFiles.UserSettings);
    private Button? _recordingEditor;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _captureReleaseTimer;
    private VirtualKey _lastPressedKey = VirtualKey.None;
    private int _captureReleaseTicks;

    /// <summary>
    /// True while the settings page owns the keyboard. The global poller reads
    /// the raw keyboard state, so it has to stand down while the user presses
    /// the combination they want to bind: otherwise recording "Ctrl+Alt+I"
    /// would open the quick-input overlay behind this page. The flag also stays
    /// set for the moment after a capture, until those keys are released:
    /// otherwise the chord just recorded would fire the instant the field
    /// closed, because the poller only needs the keys to still be down.
    /// </summary>
    public static bool IsHotkeyCaptureActive { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
        UpdateStorageNotice();
        Loaded += (_, _) => LoadSettings();
        Loaded += (_, _) => QueueResponsiveLayout();
        Unloaded += (_, _) => CancelHotkeyCapture(null);
        HotkeyGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        OscGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        OscHeaderGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
    }

    /// <summary>
    /// Says where settings live. The portable layout keeps them beside the
    /// executable, so an install that had to fall back to the user profile must
    /// not do it silently.
    /// </summary>
    private void UpdateStorageNotice()
    {
        if (StorageNotice is null) return;
        StorageNotice.Text = PortableStorage.UsesUserProfile
            ? $"程序目录不可写，设置与模型已改存到 {PortableStorage.DataDirectory}（便携模式未生效）。"
            : $"设置与模型保存在程序目录的 {PortableStorage.DataDirectory}";
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
            // Wide enough for the longest supported chord (four modifiers plus a
            // primary key) rendered as keycaps, so nothing is clipped.
            HotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
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

    private static void SetHotkeyRow(TextBlock label, Button editor, TextBlock hint, int row, bool showHint)
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
        RenderSavedHotkey(QuickInputHotkeyBox);
        RenderSavedHotkey(VoiceHotkeyBox);
        RenderSavedHotkey(SelfVoiceHotkeyBox);
        OscHostBox.Text = settings.OscHost;
        OscPortBox.Text = settings.OscPort.ToString();
        OscIncludeOriginalToggle.IsOn = settings.IncludeOriginalInOsc;
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

        CancelHotkeyCapture(null);
        if (!TrySaveHotkeys("Ctrl+Alt+I", "F7", "Ctrl+F8", out var error))
        {
            ShowHotkeyStatus(error, HotkeyStatusKind.Error);
            ShowResult(error, InfoBarSeverity.Warning);
            return;
        }

        RenderSavedHotkey(QuickInputHotkeyBox);
        RenderSavedHotkey(VoiceHotkeyBox);
        RenderSavedHotkey(SelfVoiceHotkeyBox);
        ShowHotkeyStatus("已恢复默认快捷键：Ctrl+Alt+I / F7 / Ctrl+F8。", HotkeyStatusKind.Success);
    }

    private void OnSettingTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!TrySave(_savedSettings, out var error)) ShowResult(error, InfoBarSeverity.Warning);
    }

    /// <summary>
    /// The chatbox content choice is stored with the connection and applied to the
    /// next send; saving it immediately keeps it in step with the host and port
    /// boxes, which have no separate save button either.
    /// </summary>
    private void OnOscIncludeOriginalToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!TrySave(_savedSettings, out var error)) ShowResult(error, InfoBarSeverity.Warning);
    }

    // ---- Shortcut capture -------------------------------------------------
    //
    // The three editors are capture fields rather than text boxes. A WinUI
    // TextBox draws its own delete button, which looked like a stray "x" inside
    // the field and wiped the gesture without a word; it has no public API to
    // turn that button off. Recording the chord directly also removes the
    // uncommitted-text window: a gesture is validated and saved the moment it
    // is pressed, so nothing can be silently dropped when focus moves on.

    private void OnHotkeyCaptureClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button editor) StartHotkeyCapture(editor);
    }

    private void OnHotkeyCaptureLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Button editor || _recordingEditor != editor) return;
        CancelHotkeyCapture($"已取消录制：“{GetHotkeyLabel(editor)}”保持原快捷键。");
    }

    private void OnHotkeyCapturePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button editor) return;

        if (_recordingEditor != editor)
        {
            // Tab reaches the field; Enter or space starts recording there,
            // exactly like a click does.
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                e.Handled = true;
                StartHotkeyCapture(editor);
            }

            return;
        }

        e.Handled = true;
        _lastPressedKey = e.Key;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                CancelHotkeyCapture($"已取消录制：“{GetHotkeyLabel(editor)}”保持原快捷键。");
                return;
            case VirtualKey.Back:
            case VirtualKey.Delete:
                ApplyHotkey(editor, string.Empty);
                return;
        }

        if (IsModifierKey(e.Key))
        {
            // Holding only Ctrl/Alt/Shift/Win does not end the recording; the
            // field shows what is held so far.
            RenderHotkey(editor, string.Empty, recording: true);
            return;
        }

        ApplyHotkey(editor, BuildGesture(e.Key));
    }

    /// <summary>
    /// Releasing a modifier while recording redraws the intermediate state:
    /// otherwise the field keeps claiming a key that is no longer held.
    /// </summary>
    private void OnHotkeyCapturePreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button editor || _recordingEditor != editor) return;
        if (!IsModifierKey(e.Key)) return;
        e.Handled = true;
        RenderHotkey(editor, string.Empty, recording: true);
    }

    private void StartHotkeyCapture(Button editor)
    {
        if (_loading || _recordingEditor == editor) return;
        CancelHotkeyCapture(null);

        _recordingEditor = editor;
        _lastPressedKey = VirtualKey.None;
        IsHotkeyCaptureActive = true;
        SetFieldBorder(editor, RecordingFieldBorder);
        RenderHotkey(editor, string.Empty, recording: true);
        ShowHotkeyStatus($"“{GetHotkeyLabel(editor)}”：请按下新的快捷键。Esc 取消，Backspace 清除。", HotkeyStatusKind.Info);
        editor.Focus(FocusState.Programmatic);
    }

    private void CancelHotkeyCapture(string? message)
    {
        var editor = _recordingEditor;
        _recordingEditor = null;
        if (editor is null) return;

        ScheduleCaptureGateRelease();
        RenderSavedHotkey(editor);
        if (message is not null) ShowHotkeyStatus(message, HotkeyStatusKind.Info);
    }

    /// <summary>
    /// Stores a captured chord. <paramref name="gesture"/> is null when the key
    /// that was pressed can never be a shortcut primary key.
    /// </summary>
    private void ApplyHotkey(Button editor, string? gesture)
    {
        if (gesture is null)
        {
            RejectCapture(editor, "不支持这个按键：主键必须是字母、数字或 F1–F12。", null);
            return;
        }

        var normalized = gesture;
        if (gesture.Length > 0)
        {
            try
            {
                normalized = HotkeyBinding.Normalize(gesture);
            }
            catch (ArgumentException)
            {
                RejectCapture(editor, $"不支持组合“{FormatGesture(gesture)}”：修饰键不能重复，主键必须是字母、数字或 F1–F12。", gesture);
                return;
            }
        }

        var (quickInput, voice, selfVoice) = PendingHotkeys(editor, normalized);
        if (!TrySaveHotkeys(quickInput, voice, selfVoice, out var error))
        {
            RejectCapture(editor, error, null);
            return;
        }

        _recordingEditor = null;
        ScheduleCaptureGateRelease();
        SetFieldBorder(editor, IdleFieldBorder);
        RenderHotkey(editor, normalized, recording: false);
        ShowHotkeyStatus(
            normalized.Length == 0
                ? $"已清空：“{GetHotkeyLabel(editor)}”现在没有快捷键。"
                : $"已保存：“{GetHotkeyLabel(editor)}”现在是 {FormatGesture(normalized)}。",
            HotkeyStatusKind.Success);
    }

    /// <summary>
    /// Keeps recording with a red frame: the offending chord stays visible and
    /// the status line says why, so a rejected edit never looks like a silent
    /// revert to the old value.
    /// </summary>
    private void RejectCapture(Button editor, string message, string? rejectedGesture)
    {
        SetFieldBorder(editor, ErrorFieldBorder);
        ShowHotkeyStatus(message, HotkeyStatusKind.Error);
        RenderHotkey(editor, rejectedGesture ?? string.Empty, recording: rejectedGesture is null);
    }

    private (string QuickInput, string Voice, string SelfVoice) PendingHotkeys(Button editor, string value) =>
        editor == QuickInputHotkeyBox
            ? (value, GetSavedHotkey(VoiceHotkeyBox), GetSavedHotkey(SelfVoiceHotkeyBox))
            : editor == VoiceHotkeyBox
                ? (GetSavedHotkey(QuickInputHotkeyBox), value, GetSavedHotkey(SelfVoiceHotkeyBox))
                : (GetSavedHotkey(QuickInputHotkeyBox), GetSavedHotkey(VoiceHotkeyBox), value);

    private static string? BuildGesture(VirtualKey key)
    {
        var primary = DescribePrimary(key);
        if (primary is null) return null;

        var parts = new List<string>(5);
        if (IsKeyDown(VirtualKey.Control)) parts.Add("CTRL");
        if (IsKeyDown(VirtualKey.Menu)) parts.Add("ALT");
        if (IsKeyDown(VirtualKey.Shift)) parts.Add("SHIFT");
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) parts.Add("WIN");
        parts.Add(primary);
        return string.Join('+', parts);
    }

    private static string? DescribePrimary(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + ((int)key - (int)VirtualKey.A))).ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + ((int)key - (int)VirtualKey.Number0))).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (((int)key - (int)VirtualKey.F1) + 1),
        _ => null
    };

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows or
        VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftShift or VirtualKey.RightShift;

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    /// <summary>
    /// Hands the keyboard back to the global poller once the keys used for the
    /// capture are physically up again, so the freshly stored chord cannot fire
    /// by itself. The tick limit is a safety net: a stuck gate would silently
    /// disable every global shortcut.
    /// </summary>
    private void ScheduleCaptureGateRelease()
    {
        if (DispatcherQueue is null)
        {
            IsHotkeyCaptureActive = false;
            return;
        }

        if (_captureReleaseTimer is null)
        {
            _captureReleaseTimer = DispatcherQueue.CreateTimer();
            _captureReleaseTimer.Interval = TimeSpan.FromMilliseconds(60);
            _captureReleaseTimer.IsRepeating = true;
            _captureReleaseTimer.Tick += (_, _) =>
            {
                if (_recordingEditor is not null)
                {
                    _captureReleaseTicks = 0;
                    return;
                }

                _captureReleaseTicks++;
                if (_captureReleaseTicks < 50 && AnyCaptureKeyDown()) return;

                _captureReleaseTicks = 0;
                IsHotkeyCaptureActive = false;
                _captureReleaseTimer?.Stop();
            };
        }

        _captureReleaseTicks = 0;
        _captureReleaseTimer.Start();
    }

    private bool AnyCaptureKeyDown() =>
        IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu) || IsKeyDown(VirtualKey.Shift) ||
        IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows) ||
        (_lastPressedKey != VirtualKey.None && IsKeyDown(_lastPressedKey));

    private void RenderSavedHotkey(Button editor)
    {
        SetFieldBorder(editor, IdleFieldBorder);
        var saved = GetSavedHotkey(editor);
        string display;
        try { display = saved.Length == 0 ? string.Empty : HotkeyBinding.Normalize(saved); }
        catch (ArgumentException) { display = saved; } // keep a legacy value visible so it can be replaced
        RenderHotkey(editor, display, recording: false);
    }

    private void RenderHotkey(Button editor, string gesture, bool recording)
    {
        var host = KeysHost(editor);
        host.Children.Clear();

        if (recording)
        {
            var held = HeldModifiers();
            if (held.Count == 0)
            {
                host.Children.Add(Placeholder("请按下快捷键…"));
                return;
            }

            foreach (var modifier in held) host.Children.Add(Keycap(modifier));
            host.Children.Add(Placeholder("…"));
            return;
        }

        if (string.IsNullOrWhiteSpace(gesture))
        {
            host.Children.Add(Placeholder("未设置"));
            return;
        }

        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            host.Children.Add(Keycap(FormatKey(part)));
        }
    }

    private static IReadOnlyList<string> HeldModifiers()
    {
        var held = new List<string>(4);
        if (IsKeyDown(VirtualKey.Control)) held.Add("Ctrl");
        if (IsKeyDown(VirtualKey.Menu)) held.Add("Alt");
        if (IsKeyDown(VirtualKey.Shift)) held.Add("Shift");
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) held.Add("Win");
        return held;
    }

    private static string FormatKey(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => "Ctrl",
        "ALT" or "MENU" => "Alt",
        "SHIFT" => "Shift",
        "WIN" or "WINDOWS" => "Win",
        _ => part.ToUpperInvariant()
    };

    private static string FormatGesture(string gesture) =>
        string.Join('+', gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(FormatKey));

    private static Border Keycap(string text) => new()
    {
        Background = KeycapBackground,
        BorderBrush = KeycapBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(9, 3, 9, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = KeycapForeground
        }
    };

    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Microsoft YaHei UI"),
        FontSize = 14,
        Foreground = PlaceholderForeground,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void SetFieldBorder(Button editor, Brush brush) => FieldHost(editor).BorderBrush = brush;

    private void ShowHotkeyStatus(string message, HotkeyStatusKind kind)
    {
        if (HotkeyStatus is null) return;
        HotkeyStatus.Text = message;
        HotkeyStatus.Foreground = kind switch
        {
            HotkeyStatusKind.Error => ErrorStatusForeground,
            HotkeyStatusKind.Success => SuccessStatusForeground,
            _ => NeutralStatusForeground
        };
        HotkeyStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Writes the three shortcuts together with the OSC boxes. The shortcut
    /// values are already canonical (or empty, which means 未设置), so the only
    /// checks left are the accepted-key rules and conflicts between the three.
    /// </summary>
    private bool TrySaveHotkeys(string quickInput, string voice, string selfVoice, out string error)
    {
        var source = new UserSettings
        {
            QuickInputHotkey = quickInput,
            VoiceHotkey = voice,
            SelfVoiceHotkey = selfVoice,
            OscIntervalSeconds = _savedSettings.OscIntervalSeconds,
            PlaySound = _savedSettings.PlaySound,
            KeepRunning = _savedSettings.KeepRunning,
            IncludeOriginalInOsc = _savedSettings.IncludeOriginalInOsc
        };
        return TrySave(source, out error);
    }

    private bool TrySave(UserSettings hotkeySource, out string error)
    {
        if (_loading)
        {
            error = string.Empty;
            return false;
        }

        if (!int.TryParse(OscPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            error = "端口范围应为 1 到 65535。";
            return false;
        }

        var settings = new UserSettings
        {
            QuickInputHotkey = NullSafe(hotkeySource.QuickInputHotkey),
            VoiceHotkey = NullSafe(hotkeySource.VoiceHotkey),
            SelfVoiceHotkey = NullSafe(hotkeySource.SelfVoiceHotkey),
            OscHost = string.IsNullOrWhiteSpace(OscHostBox.Text) ? "127.0.0.1" : OscHostBox.Text.Trim(),
            OscPort = port,
            OscIntervalSeconds = hotkeySource.OscIntervalSeconds,
            PlaySound = hotkeySource.PlaySound,
            KeepRunning = hotkeySource.KeepRunning,
            // 这个开关的状态来自界面（开关自己触发的保存），而不是只从内存里抄一份。
            IncludeOriginalInOsc = OscIncludeOriginalToggle.IsOn
        };
        if (!TryValidateHotkeys(settings, out error)) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            _savedSettings = settings;
            ((App)global::Microsoft.UI.Xaml.Application.Current).State.ReloadOscSettings();
            SettingsInfo.IsOpen = false;
            error = string.Empty;
            return true;
        }
        catch (IOException exception)
        {
            error = $"设置保存失败：{exception.Message}";
            return false;
        }
    }

    private static bool TryValidateHotkeys(UserSettings settings, out string error)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in new[]
        {
            ("打开输入框", settings.QuickInputHotkey),
            ("他人语音", settings.VoiceHotkey),
            ("自身语音", settings.SelfVoiceHotkey)
        })
        {
            // An empty gesture is a deliberate 未设置 from the settings page:
            // that action has no shortcut, so it neither normalizes nor conflicts.
            if (string.IsNullOrWhiteSpace(pair.Item2)) continue;

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

    private static string NullSafe(string? value) => value?.Trim() ?? string.Empty;

    private string GetSavedHotkey(Button editor)
    {
        var value = editor == QuickInputHotkeyBox
            ? _savedSettings.QuickInputHotkey
            : editor == VoiceHotkeyBox
                ? _savedSettings.VoiceHotkey
                : _savedSettings.SelfVoiceHotkey;
        return NullSafe(value);
    }

    private string GetHotkeyLabel(Button editor) => editor == QuickInputHotkeyBox ? "打开输入框" : editor == VoiceHotkeyBox ? "他人语音" : "自身语音";

    private Border FieldHost(Button editor) => editor == QuickInputHotkeyBox ? QuickInputHotkeyField : editor == VoiceHotkeyBox ? VoiceHotkeyField : SelfVoiceHotkeyField;

    private StackPanel KeysHost(Button editor) => editor == QuickInputHotkeyBox ? QuickInputHotkeyKeys : editor == VoiceHotkeyBox ? VoiceHotkeyKeys : SelfVoiceHotkeyKeys;

    // The capture field reuses the palette the page already uses for inputs and
    // chips, so it reads as one more field instead of a stray button.
    private static readonly SolidColorBrush IdleFieldBorder = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xD4, 0xDF, 0xEC));
    private static readonly SolidColorBrush RecordingFieldBorder = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x27, 0x74, 0xC7));
    private static readonly SolidColorBrush ErrorFieldBorder = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush KeycapBackground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xEE, 0xF3, 0xF8));
    private static readonly SolidColorBrush KeycapBorder = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xD4, 0xDF, 0xEC));
    private static readonly SolidColorBrush KeycapForeground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x16, 0x20, 0x2E));
    private static readonly SolidColorBrush PlaceholderForeground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x5B, 0x6D, 0x82));
    private static readonly SolidColorBrush NeutralStatusForeground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x5B, 0x6D, 0x82));
    private static readonly SolidColorBrush SuccessStatusForeground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x16, 0x86, 0x6E));
    private static readonly SolidColorBrush ErrorStatusForeground = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xC4, 0x2B, 0x1C));

    private enum HotkeyStatusKind
    {
        Info,
        Success,
        Error
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

        [System.Text.Json.Serialization.JsonPropertyName(OscChatboxSettings.IncludeOriginalPropertyName)]
        public bool IncludeOriginalInOsc { get; set; } = OscChatboxSettings.DefaultIncludeOriginal;
    }
}
