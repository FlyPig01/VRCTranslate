using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using VrcTranslate.Application.Subtitles;
using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Storage;
using VrcTranslate.Desktop.Pages;
using System.Runtime.InteropServices;

namespace VrcTranslate.Desktop;

public sealed partial class MainWindow : Window
{
    private bool _windowConfigured;
    private Button? _selectedNavigationButton;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hotkeyTimer;
    private readonly HashSet<string> _hotkeysDown = new(StringComparer.OrdinalIgnoreCase);
    private bool _overlaysShown;

    public MainWindow()
    {
        InitializeComponent();
        Title = "VRCTranslate";
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        var smokePage = Environment.GetEnvironmentVariable("VRC_TRANSLATE_SMOKE_PAGE");
        var smoke = smokePage?.ToLowerInvariant() switch
        {
            "input" => (InputNavButton, typeof(SelfMessagePage), "输入"),
            "voice" => (VoiceNavButton, typeof(VoicePage), "语音"),
            "translation" => (TranslationNavButton, typeof(TranslationPage), "翻译"),
            "settings" => (SettingsNavButton, typeof(SettingsPage), "设置"),
            "guide" => (GuideNavButton, typeof(GuidePage), "指南"),
            _ => (RunNavButton, typeof(RunPage), "运行")
        };
        SetSelected(smoke.Item1);
        PageHeader.Text = smoke.Item3;
        ContentFrame.Navigate(smoke.Item2);
        var state = ((App)Microsoft.UI.Xaml.Application.Current).State;
        state.TranslationPreviewChanged += OnTranslationPreviewChanged;
        OverlayWindowHost.Initialize(state);
        ApplySmokeCaption();
        StartGlobalHotkeys();
    }

    /// <summary>
    /// Seeds one deterministic caption for the render smoke. Captions normally
    /// appear only after real speech is recognized, which the validation script
    /// deliberately does not wait for, so the check of where the message list sits
    /// inside the surface needs this one fixed message. Only the validation script
    /// sets the variable; a normal launch never does.
    /// <para>
    /// The probe is seeded in the "translation only" presentation: the recognized
    /// line's light blue cannot be told apart from the accent colour the same smoke
    /// keeps looking for, while the white translation can. The presentation of this
    /// surface is read from the saved settings again on the next launch.
    /// </para>
    /// </summary>
    private static void ApplySmokeCaption()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VRC_TRANSLATE_SMOKE_CAPTION"))) return;
        var subtitle = OverlayWindowHost.Subtitle;
        if (subtitle is null) return;
        subtitle.ApplyContentMode(SubtitleContentMode.TranslatedOnly);
        // Recognition is not running yet, so the surface starts paused and would drop
        // this probe. The pause is restored immediately: it only gates appending, and
        // the probe is the one message the render smoke needs.
        subtitle.SetStreamPaused(false);
        subtitle.AppendCaption("Smoke caption original line", "冒烟字幕译文");
        subtitle.SetStreamPaused(true);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Stop polling before tearing down the secondary windows. Otherwise a
        // key transition arriving during shutdown can recreate an overlay
        // after the main HWND has already been destroyed.
        _hotkeyTimer?.Stop();
        _hotkeyTimer = null;
        _hotkeysDown.Clear();

        // Recognition sessions outlive pages now, so shutdown must stop them
        // explicitly instead of relying on a page Unloaded handler.
        _ = ((App)Microsoft.UI.Xaml.Application.Current).State.ShutdownSpeechAsync();

        try
        {
            if (Microsoft.UI.Xaml.Application.Current is App app)
            {
                app.State.TranslationPreviewChanged -= OnTranslationPreviewChanged;
            }
        }
        finally
        {
            // Keep this in a finally block: application teardown can race the
            // event unsubscription, but it must never leave a top-level
            // overlay HWND alive.
            OverlayWindowHost.CloseAll();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_windowConfigured)
        {
            return;
        }

        _windowConfigured = true;
        ConfigureWindowIcon();
        ConfigureWindowPresentation();
        if (!_overlaysShown)
        {
            _overlaysShown = true;
            var state = ((App)Microsoft.UI.Xaml.Application.Current).State;
            OverlayWindowHost.ShowAtStartup(state);
            // ShowAtStartup creates the native overlay handles once. Restore
            // shell activation after that setup so the overlays do not steal
            // focus during application launch.
            DispatcherQueue.TryEnqueue(Activate);
        }
    }

    private void ConfigureWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (!File.Exists(iconPath))
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(windowId).SetIcon(iconPath);
        }
        catch
        {
            // A missing title-bar icon must never prevent the application from opening.
        }
    }

    private void ConfigureWindowPresentation()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                // AppWindow.Resize and AppWindow.Move use physical pixels while XAML
                // lays out in effective pixels. Convert the target shell size so a
                // 125%/150% display does not turn the whole app into a clipped column.
                presenter.Restore();
                var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest).WorkArea;
                var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
                var scale = dpi / 96d;
                var availableWidth = workArea.Width / scale;
                var availableHeight = workArea.Height / scale;
                var logicalWidth = Math.Min(1480d, Math.Max(960d, availableWidth - 80d));
                var logicalHeight = Math.Min(900d, Math.Max(640d, availableHeight - 80d));
                var width = Math.Min(workArea.Width - 20, Math.Max(640, (int)Math.Round(logicalWidth * scale)));
                var height = Math.Min(workArea.Height - 20, Math.Max(480, (int)Math.Round(logicalHeight * scale)));
                appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
                appWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.X + (workArea.Width - width) / 2,
                    workArea.Y + (workArea.Height - height) / 2));
            }
        }
        catch
        {
            // Window sizing must never prevent the shell from opening.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void OnNavigationButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        (Type pageType, string title) = tag switch
        {
            "input" => (typeof(SelfMessagePage), "输入"),
            "translation" => (typeof(TranslationPage), "翻译"),
            "voice" => (typeof(VoicePage), "语音"),
            "guide" => (typeof(GuidePage), "指南"),
            "settings" => (typeof(SettingsPage), "设置"),
            _ => (typeof(RunPage), "运行")
        };

        SetSelected(button);
        PageHeader.Text = title;
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void StartGlobalHotkeys()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        _hotkeyTimer = queue.CreateTimer();
        _hotkeyTimer.Interval = TimeSpan.FromMilliseconds(50);
        _hotkeyTimer.Tick += (_, _) => PollGlobalHotkeys();
        _hotkeyTimer.Start();
    }

    private void PollGlobalHotkeys()
    {
        var settings = ReadHotkeys();
        foreach (var pair in settings)
        {
            var down = IsGestureDown(pair.Key);
            if (down && _hotkeysDown.Add(pair.Key))
            {
                _ = HandleHotkeyAsync(pair.Value);
            }
            else if (!down)
            {
                _hotkeysDown.Remove(pair.Key);
            }
        }
    }

    private async Task HandleHotkeyAsync(string action)
    {
        switch (action)
        {
            case "input":
                var state = ((App)Microsoft.UI.Xaml.Application.Current).State;
                OverlayWindowHost.ToggleQuickInput(state);
                break;
            case "voice":
                // 「他人语音」is the whole other-player feature: one press turns
                // recognition and the caption surface on, the next turns both
                // off, and both effects run on the one serialized switch.
                var captionState = ((App)Microsoft.UI.Xaml.Application.Current).State;
                var captionOutcome = await captionState.OtherPlayerCaption.ToggleAsync();
                if (captionOutcome == OtherPlayerCaptionToggleOutcome.ModelNotReady)
                {
                    // A refused shortcut must say why instead of doing nothing
                    // visible: bring the voice page forward and use its notice.
                    if (ContentFrame.Content is not VoicePage)
                    {
                        SetSelected(VoiceNavButton);
                        PageHeader.Text = "语音";
                        ContentFrame.Navigate(typeof(VoicePage));
                        await Task.Yield();
                    }
                    if (ContentFrame.Content is VoicePage voicePage) voicePage.ShowModelNotReadyHint();
                }
                break;
            case "self-voice":
                if (ContentFrame.Content is not SelfMessagePage)
                {
                    SetSelected(InputNavButton);
                    PageHeader.Text = "输入";
                    ContentFrame.Navigate(typeof(SelfMessagePage));
                    await Task.Yield();
                }
                if (ContentFrame.Content is SelfMessagePage selfVoice) selfVoice.ToggleSelfVoiceFromHotkey();
                break;
        }
    }

    private void OnTranslationPreviewChanged(object? sender, EventArgs e)
    {
        // Translation providers intentionally continue off the UI thread. The
        // preview event can therefore arrive from a worker thread; marshal the
        // small overlay update before touching WinUI objects.
        void UpdatePreview()
        {
            var quickInputWindow = OverlayWindowHost.QuickInput;
            if (quickInputWindow is null) return;
            var state = ((App)Microsoft.UI.Xaml.Application.Current).State;
            quickInputWindow.SetPreview(
                state.LastOriginalText,
                state.LastTranslatedText,
                state.LastSecondaryTranslatedText);
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdatePreview();
        }
        else
        {
            DispatcherQueue.TryEnqueue(UpdatePreview);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadHotkeys()
    {
        var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
        try
        {
            if (File.Exists(path))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(path));
                if (settings is not null)
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Normalize(settings.QuickInputHotkey, "CTRL+ALT+I")] = "input",
                        [Normalize(settings.VoiceHotkey, "F7")] = "voice",
                        [Normalize(settings.SelfVoiceHotkey, "CTRL+F8")] = "self-voice"
                    };
            }
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CTRL+ALT+I"] = "input", ["F7"] = "voice", ["CTRL+F8"] = "self-voice"
        };
    }

    private static string Normalize(string? value, string fallback)
    {
        try
        {
            return HotkeyBinding.Normalize(string.IsNullOrWhiteSpace(value) ? fallback : value);
        }
        catch (ArgumentException)
        {
            return HotkeyBinding.Normalize(fallback);
        }
    }

    private static bool IsGestureDown(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var key = parts.LastOrDefault();
        if (key is null) return false;
        if (parts.Any(x => x is "CTRL" or "CONTROL") && !IsDown(0x11)) return false;
        if (parts.Any(x => x is "ALT" or "MENU") && !IsDown(0x12)) return false;
        if (parts.Any(x => x is "SHIFT") && !IsDown(0x10)) return false;
        if (parts.Any(x => x is "WIN" or "WINDOWS") && !IsDown(0x5B)) return false;
        var vk = key switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75,
            "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ when key.Length == 1 => char.ToUpperInvariant(key[0]), _ => 0
        };
        return vk != 0 && IsDown(vk);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private sealed class HotkeySettings
    {
        public string QuickInputHotkey { get; set; } = "Ctrl+Alt+I";
        public string VoiceHotkey { get; set; } = "F7";
        public string SelfVoiceHotkey { get; set; } = "Ctrl+F8";
    }

    private void SetSelected(Button selected)
    {
        _selectedNavigationButton = selected;
        foreach (var button in new[] { RunNavButton, InputNavButton, VoiceNavButton, TranslationNavButton, SettingsNavButton, GuideNavButton })
        {
            ApplyNavigationVisual(button, button == selected);
        }
    }

    private void OnNavPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && button != _selectedNavigationButton)
        {
            ApplyNavigationVisual(button, isSelected: false, isHovered: true);
        }
    }

    private void OnNavPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyNavigationVisual(button, button == _selectedNavigationButton);
        }
    }

    private static void ApplyNavigationVisual(Button? button, bool isSelected, bool isHovered = false)
    {
        if (button is null) return;
        var background = isSelected
            ? Microsoft.UI.ColorHelper.FromArgb(255, 96, 145, 232)
            : isHovered
                ? Microsoft.UI.ColorHelper.FromArgb(255, 29, 41, 64)
                : Microsoft.UI.Colors.Transparent;
        var foreground = isSelected || isHovered
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.ColorHelper.FromArgb(255, 200, 213, 232);
        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foreground);
        button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(background);
        button.Foreground = brush;
        // ContentPresenter does not reliably inherit Button.Foreground in WinUI.
        // Set both icon and label explicitly so hover and selected states stay readable.
        if (button.Content is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control control)
                {
                    control.Foreground = brush;
                }
            }
        }
    }
}
