using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Desktop.Pages;

/// <summary>Controls the VRChat voice-caption workflow.</summary>
public sealed partial class VoicePage : Page
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCTranslate", "v2-voice-settings.json");
    private readonly AppState _state;
    private VoiceSettings _settings = new();
    private bool _loaded;
    private bool _overlayAppearanceReady;
    private bool _updatingOverlayAppearance;
    private bool _speechEventsAttached;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _audioLevelTimer;
    private double _audioLevel;
    private DateTimeOffset _lastAudioFrameAt;

    private AppState State => _state;

    /// <summary>Recognition outlives this page; the session host owns the running state.</summary>
    private bool IsRunning => _state.SubtitleVoice.IsRunning;

    public VoicePage()
    {
        InitializeComponent();
        _state = ((App)global::Microsoft.UI.Xaml.Application.Current).State;
        _audioLevelTimer = DispatcherQueue.CreateTimer();
        _audioLevelTimer.Interval = TimeSpan.FromMilliseconds(90);
        _audioLevelTimer.Tick += (_, _) => DecayAudioLevel();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        VoiceStatusGrid.SizeChanged += (_, _) => LayoutStatusControls(VoiceStatusGrid.ActualWidth);
        VoiceStatusActions.SizeChanged += (_, _) => LayoutStatusControls(VoiceStatusGrid.ActualWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_overlayAppearanceReady)
            State.OverlayAppearance.Changed += OnOverlayAppearanceChanged;
        _overlayAppearanceReady = true;
        AttachSpeechEvents();
        LoadSettings();
        UpdateLocalModelStatus();
        UpdateRunningVisuals();
        UpdateSubtitleOverlayOpacity();
        _loaded = true;
        LayoutStatusControls(PageScrollViewer?.ActualWidth ?? ContentColumn?.ActualWidth ?? 0);
        DispatcherQueue.TryEnqueue(() => LayoutStatusControls(VoiceStatusGrid.ActualWidth > 0 ? VoiceStatusGrid.ActualWidth : ContentColumn?.ActualWidth ?? 0));
        DispatcherQueue.TryEnqueue(() => LayoutStatusControls(VoiceStatusGrid.ActualWidth));
    }

    private void AttachSpeechEvents()
    {
        if (_speechEventsAttached) return;
        _speechEventsAttached = true;
        State.SubtitleVoice.RunningChanged += OnSessionRunningChanged;
        State.SubtitleVoice.Notified += OnSessionNotified;
        State.SubtitleVoice.LevelChanged += OnAudioLevelChanged;
    }

    private void DetachSpeechEvents()
    {
        if (!_speechEventsAttached) return;
        _speechEventsAttached = false;
        State.SubtitleVoice.RunningChanged -= OnSessionRunningChanged;
        State.SubtitleVoice.Notified -= OnSessionNotified;
        State.SubtitleVoice.LevelChanged -= OnAudioLevelChanged;
    }

    private void OnSessionRunningChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateRunningVisuals);

    private void OnSessionNotified(object? sender, SpeechNotificationEventArgs notification) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsRunning) return;
            ShowInfo(notification.Title.Length > 0 ? $"{notification.Title}：{notification.Message}" : notification.Message,
                notification.Kind switch
                {
                    SpeechNotificationKind.Success => InfoBarSeverity.Success,
                    SpeechNotificationKind.Error => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Warning
                });
        });

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _overlayAppearanceReady = false;
        State.OverlayAppearance.Changed -= OnOverlayAppearanceChanged;
        // Recognition continues in the application-scoped session host; only
        // the UI subscriptions are released with the page.
        DetachSpeechEvents();
    }

    private void OnOverlayAppearanceChanged(object? sender, EventArgs e)
    {
        if (!_overlayAppearanceReady || _updatingOverlayAppearance) return;
        if (DispatcherQueue.HasThreadAccess) UpdateSubtitleOverlayOpacity();
        else DispatcherQueue.TryEnqueue(UpdateSubtitleOverlayOpacity);
    }

    private void UpdateSubtitleOverlayOpacity()
    {
        if (!_overlayAppearanceReady) return;
        _updatingOverlayAppearance = true;
        try
        {
            var percent = Math.Clamp(State.OverlayAppearance.Current.SubtitleOverlayOpacity * 100d, 60d, 100d);
            SubtitleOverlayOpacitySlider.Value = percent;
            SubtitleOverlayOpacityValue.Text = $"{Math.Round(percent):0}%";
        }
        finally
        {
            _updatingOverlayAppearance = false;
        }
    }

    private void OnSubtitleOverlayOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_overlayAppearanceReady || _updatingOverlayAppearance) return;
        _updatingOverlayAppearance = true;
        try
        {
            State.OverlayAppearance.SetSubtitleOpacity(e.NewValue / 100d);
            var percent = Math.Clamp(State.OverlayAppearance.Current.SubtitleOverlayOpacity * 100d, 60d, 100d);
            SubtitleOverlayOpacitySlider.Value = percent;
            SubtitleOverlayOpacityValue.Text = $"{Math.Round(percent):0}%";
        }
        finally
        {
            _updatingOverlayAppearance = false;
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        var width = PageScrollViewer?.ActualWidth ?? e.NewSize.Width;
        if (width > 0) ContentColumn.Width = Math.Min(760, Math.Max(1, width - 56));

        LayoutStatusControls(width);
    }

    private void LayoutStatusControls(double width)
    {
        if (VoiceStatusGrid is null || VoiceStatusActions is null || width <= 0) return;

        // Use the measured content column instead of the grid's transient
        // width. During the first XAML measure pass the action column can
        // report zero, which previously left a normal window in the compact
        // vertical arrangement until the page was revisited.
        var available = ContentColumn?.ActualWidth > 0 ? ContentColumn.ActualWidth : width;
        var compact = available < 470;
        VoiceStatusActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        VoiceStatusActions.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        VoiceStatusActions.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(14, 0, 0, 0);
        if (compact)
        {
            VoiceStatusGrid.ColumnDefinitions.Clear();
            VoiceStatusGrid.RowDefinitions.Clear();
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(VoiceStatusDotHost, 0);
            Grid.SetRow(VoiceStatusDotHost, 0);
            Grid.SetColumn(VoiceStatusText, 1);
            Grid.SetRow(VoiceStatusText, 0);
            Grid.SetColumn(VoiceStatusActions, 0);
            Grid.SetRow(VoiceStatusActions, 1);
            Grid.SetColumnSpan(VoiceStatusActions, 2);
            Grid.SetColumn(VoiceAudioLevel, 0);
            Grid.SetRow(VoiceAudioLevel, 2);
            Grid.SetColumnSpan(VoiceAudioLevel, 2);
            Grid.SetColumn(VoiceHotkeySummary, 0);
            Grid.SetRow(VoiceHotkeySummary, 3);
            Grid.SetColumnSpan(VoiceHotkeySummary, 2);
            VoiceHotkeySummary.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            VoiceStatusGrid.ColumnDefinitions.Clear();
            VoiceStatusGrid.RowDefinitions.Clear();
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(VoiceStatusDotHost, 0);
            Grid.SetRow(VoiceStatusDotHost, 0);
            Grid.SetColumn(VoiceStatusText, 1);
            Grid.SetRow(VoiceStatusText, 0);
            Grid.SetColumn(VoiceStatusActions, 2);
            Grid.SetRow(VoiceStatusActions, 0);
            Grid.SetColumnSpan(VoiceStatusActions, 1);
            Grid.SetColumn(VoiceAudioLevel, 1);
            Grid.SetRow(VoiceAudioLevel, 1);
            Grid.SetColumnSpan(VoiceAudioLevel, 1);
            Grid.SetColumn(VoiceHotkeySummary, 2);
            Grid.SetRow(VoiceHotkeySummary, 1);
            Grid.SetColumnSpan(VoiceHotkeySummary, 1);
            VoiceHotkeySummary.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void OnOpenOverlayClicked(object sender, RoutedEventArgs e)
    {
        OverlayWindowHost.ShowSubtitle();
    }

    private static string NormalizeSourceTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        return normalized switch
        {
            "en" or "en-us" or "en-gb" => "en",
            "ja" or "ja-jp" => "ja",
            "ko" or "ko-kr" => "ko",
            _ => "auto"
        };
    }

    private void OnSpeechModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _settings.Provider = "local";
        SaveSettings();
        UpdateLocalModelStatus();
    }

    private async void OnConfigureModelClicked(object sender, RoutedEventArgs e)
    {
        var status = State.LocalSpeech.GetModelStatus();
        if (status.State == LocalSpeechModelState.Ready && status.IsBundled)
        {
            var bundledDialog = new ContentDialog
            {
                Title = "本地语音模型",
                Content = new TextBlock
                {
                    Text = "语音模型已随程序内置（SenseVoice Small · 中 / 英 / 日 / 韩，兼容粤语），无需下载或删除。",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };
            await bundledDialog.ShowAsync();
            return;
        }

        var content = new StackPanel { Spacing = 10, Width = 430 };
        content.Children.Add(new TextBlock
        {
            Text = "SenseVoice Small",
            Style = (Style)global::Microsoft.UI.Xaml.Application.Current.Resources["SectionTitleTextStyle"]
        });
        content.Children.Add(new TextBlock
        {
            Text = "INT8 本地推理 · 中 / 英 / 日 / 韩（自动检测，兼容粤语）",
            Style = (Style)global::Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextStyle"]
        });
        var statusText = new TextBlock
        {
            Text = status.Message ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)global::Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextStyle"]
        };
        content.Children.Add(statusText);

        var ready = status.State == LocalSpeechModelState.Ready;
        var dialog = new ContentDialog
        {
            Title = "本地语音模型",
            Content = content,
            PrimaryButtonText = ready ? "删除模型" : "安装模型",
            SecondaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (ready)
            {
                await State.LocalSpeech.RemoveModelAsync();
            }
            else
            {
                ModelStatus.Text = "正在下载模型…";
                var progress = new Progress<LocalSpeechModelProgress>(value =>
                {
                    var text = value.Fraction is double fraction
                        ? $"正在下载模型 {Math.Round(fraction * 100):0}%"
                        : $"正在下载模型 {value.BytesReceived / 1048576d:0} MB";
                    DispatcherQueue.TryEnqueue(() => ModelStatus.Text = text);
                });
                await State.LocalSpeech.InstallModelAsync(progress);
            }
        }
        catch (OperationCanceledException)
        {
            ShowInfo("模型安装已取消", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowInfo("模型安装失败", InfoBarSeverity.Error);
            _ = exception;
        }

        UpdateLocalModelStatus();
    }

    private void UpdateLocalModelStatus()
    {
        var status = State.LocalSpeech.GetModelStatus();
        ModelStatus.Text = status.State switch
        {
            LocalSpeechModelState.Ready => status.IsBundled
                ? "已就绪（随程序内置）· 中 / 英 / 日 / 韩"
                : "已就绪 · 中 / 英 / 日 / 韩",
            LocalSpeechModelState.Installing => "正在安装…",
            LocalSpeechModelState.Invalid => "文件不完整，请重新安装",
            _ => "尚未安装"
        };
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (IsRunning) await StopRecognitionAsync();
        else await StartRecognitionAsync();
    }

    /// <summary>Toggles the subtitle surface from the global shortcut.</summary>
    public void ToggleSubtitleFromHotkey() => OverlayWindowHost.ToggleSubtitle();

    // Keep the old page entry point source-compatible with earlier V2 builds.
    // The shortcut now controls window visibility; recognition remains tied to
    // the explicit start/stop button on this page.
    public void ToggleRecognitionFromHotkey() => ToggleSubtitleFromHotkey();

    private async Task StartRecognitionAsync()
    {
        try
        {
            var status = State.LocalSpeech.GetModelStatus();
            if (status.State != LocalSpeechModelState.Ready)
            {
                ShowInfo("请先安装本地模型", InfoBarSeverity.Warning);
                UpdateLocalModelStatus();
                return;
            }

            // VRChat audio is received through the Windows loopback adapter;
            // segmentation, SenseVoice inference, translation and OSC output
            // stay in the application-scoped session host.
            await State.SubtitleVoice.StartAsync();
            UpdateRunningVisuals();
        }
        catch (Exception exception)
        {
            UpdateRunningVisuals();
            ShowInfo("无法读取系统音频，请检查音频设备后重试。", InfoBarSeverity.Warning);
            _ = exception;
        }
    }

    private async Task StopRecognitionAsync()
    {
        try
        {
            await State.SubtitleVoice.StopAsync();
        }
        finally
        {
            _audioLevelTimer.Stop();
            _audioLevel = 0;
            VoiceAudioLevel.Value = 0;
            UpdateRunningVisuals();
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs args)
    {
        _lastAudioFrameAt = DateTimeOffset.UtcNow;
        var normalized = Math.Clamp((args.Rms * 280f) + (args.Peak * 20f), 0f, 100f);
        _audioLevel = Math.Max(_audioLevel * 0.35, normalized);
        if (DispatcherQueue.HasThreadAccess)
            VoiceAudioLevel.Value = _audioLevel;
        else
            DispatcherQueue.TryEnqueue(() => VoiceAudioLevel.Value = _audioLevel);
        if (!_audioLevelTimer.IsRunning) _audioLevelTimer.Start();
    }

    private void DecayAudioLevel()
    {
        if (!IsRunning)
        {
            _audioLevelTimer.Stop();
            VoiceAudioLevel.Value = 0;
            return;
        }

        if (DateTimeOffset.UtcNow - _lastAudioFrameAt > TimeSpan.FromMilliseconds(220))
        {
            _audioLevel *= 0.72;
            VoiceAudioLevel.Value = _audioLevel;
        }
        if (_audioLevel < 0.5 && DateTimeOffset.UtcNow - _lastAudioFrameAt > TimeSpan.FromSeconds(1))
            _audioLevelTimer.Stop();
    }

    /// <summary>Recognition hook kept for the validation scripts; runs through the session host.</summary>
    public Task RecognizeLocalSamplesAsync(
        ReadOnlyMemory<float> samples,
        string sourceLanguage,
        CancellationToken cancellationToken = default) =>
        State.SubtitleVoice.RecognizeSamplesAsync(
            samples, NormalizeSourceTag(sourceLanguage), cancellationToken);

    private void UpdateRunningVisuals()
    {
        var active = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 33, 165, 116));
        var inactive = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 154, 168, 184));
        VoiceStatusText.Text = IsRunning ? "字幕翻译中" : "字幕已停止";
        VoiceStartButton.Content = IsRunning ? "停止" : "开始";
        AutomationProperties.SetName(VoiceStartButton, IsRunning ? "停止识别" : "开始识别");
        VoiceStatusDot.Fill = IsRunning ? active : inactive;
        VoicePulse.IsActive = IsRunning;
        VoicePulse.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        VoiceStatusPanel.Background = new SolidColorBrush(IsRunning
            ? Microsoft.UI.ColorHelper.FromArgb(255, 235, 248, 241)
            : Microsoft.UI.ColorHelper.FromArgb(255, 234, 242, 255));
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                _settings = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(_settingsPath)) ?? new VoiceSettings();
            }
        }
        catch
        {
            _settings = new VoiceSettings();
        }

        _settings.Provider = "local";
        SpeechModelBox.SelectedIndex = 0;
        VoiceHotkeyValue.Text = ReadGlobalVoiceHotkey();
        AutomationProperties.SetName(VoiceHotkeyValue, VoiceHotkeyValue.Text);
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only settings folder must not close the page.
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        VoiceInfo.Title = message;
        VoiceInfo.Message = string.Empty;
        VoiceInfo.Severity = severity;
        VoiceInfo.IsOpen = true;
    }

    private static string ReadGlobalVoiceHotkey()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCTranslate", "v2-user-settings.json");
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<GlobalHotkeySettings>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(settings?.VoiceHotkey)) return settings.VoiceHotkey.Trim();
            }
        }
        catch
        {
            // A partially edited settings file should leave the visible default
            // shortcut intact.
        }

        return "F7";
    }

    private sealed class VoiceSettings
    {
        public string Provider { get; set; } = "local";
    }

    private sealed class GlobalHotkeySettings
    {
        public string VoiceHotkey { get; set; } = "F7";
    }
}
