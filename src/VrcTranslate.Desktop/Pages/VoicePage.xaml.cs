using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Infrastructure.Storage;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Core.Translation;
using VrcTranslate.Desktop.Controls;

namespace VrcTranslate.Desktop.Pages;

/// <summary>Controls the VRChat voice-caption workflow.</summary>
public sealed partial class VoicePage : Page
{
    /// <summary>
    /// The one short notice a fallback earns. Shown once per fallback episode -
    /// repeats are suppressed by the session's latch - and withdrawn as soon as
    /// process audio works again.
    /// </summary>
    private const string FallbackNoticeText = "无法只采集 VRChat 声音，已改用系统声音";

    private readonly string _settingsPath = PortableStorage.GetPath(AppDataFiles.VoiceSettings);
    private readonly AppState _state;
    private VoiceSettings _settings = new();
    private bool _loaded;
    private bool _overlayAppearanceReady;
    private bool _updatingOverlayAppearance;
    private bool _speechEventsAttached;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _audioLevelTimer;
    private readonly AudioLevelMeter _audioMeter = new(new AudioLevelMeterOptions { BarCount = 24 });
    private LevelBarRenderer? _levelBars;
    private IReadOnlyList<SpeakerIdentity> _speakers = [];
    private bool _updatingSpeakerToggle;
    private bool _fallbackNoticeActive;

    private AppState State => _state;

    /// <summary>Recognition outlives this page; the session host owns the running state.</summary>
    private bool IsRunning => _state.SubtitleVoice.IsRunning;

    public VoicePage()
    {
        InitializeComponent();
        _state = ((App)global::Microsoft.UI.Xaml.Application.Current).State;
        _audioLevelTimer = DispatcherQueue.CreateTimer();
        _audioLevelTimer.Interval = TimeSpan.FromMilliseconds(60);
        _audioLevelTimer.Tick += (_, _) => AdvanceAudioLevel();
        // Wire the meter in the constructor: the first SizeChanged (0 → real
        // width) fires before Loaded, so subscribing in OnLoaded left the bar
        // count stuck at the initial 24 and the track fell short of the button.
        _levelBars = new LevelBarRenderer(VoiceAudioLevel, barCount: 24, barWidth: 6, gap: 3);
        VoiceAudioLevelHost.SizeChanged += (_, args) => ResizeAudioLevel(args.NewSize.Width);
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
        // Catch up in case the size settled before this load callback ran.
        ResizeAudioLevel(VoiceAudioLevelHost.ActualWidth);
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
        State.SubtitleVoice.SourceChanged += OnCaptureSourceChanged;
    }

    private void DetachSpeechEvents()
    {
        if (!_speechEventsAttached) return;
        _speechEventsAttached = false;
        State.SubtitleVoice.RunningChanged -= OnSessionRunningChanged;
        State.SubtitleVoice.Notified -= OnSessionNotified;
        State.SubtitleVoice.LevelChanged -= OnAudioLevelChanged;
        State.SubtitleVoice.SourceChanged -= OnCaptureSourceChanged;
    }

    private void OnSessionRunningChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateRunningVisuals);

    /// <summary>
    /// Capture source changes arrive on a capture thread, so the badge is only
    /// ever touched through the dispatcher - never from the callback itself.
    /// </summary>
    private void OnCaptureSourceChanged(object? sender, AudioSourceChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => UpdateCaptureSource(args.State, announceFallback: true));

    /// <summary>
    /// Mirrors the source the recognizer is fed from into the compact badge, and
    /// turns the first fallback of a run into exactly one short notice.
    /// </summary>
    private void UpdateCaptureSource(AudioSourceState state, bool announceFallback)
    {
        if (VoiceCaptureSourceBadge is null || VoiceCaptureSourceText is null) return;
        VoiceCaptureSourceText.Text = DescribeCaptureSource(state);
        VoiceCaptureSourceBadge.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(VoiceCaptureSourceBadge, $"当前采集来源：{VoiceCaptureSourceText.Text}");
        if (!announceFallback) return;

        if (state.IsCompatibilityMode)
        {
            // 首次激活失败只提示一次；后续重试失败不再打扰用户（采集侧会熔断）。
            if (!State.SubtitleVoice.ConsumeCaptureFallbackNotice()) return;
            ShowInfo(FallbackNoticeText, InfoBarSeverity.Warning);
            return;
        }

        // 恢复后提示自动消失。
        if (!_fallbackNoticeActive) return;
        _fallbackNoticeActive = false;
        if (VoiceInfo.IsOpen && string.Equals(VoiceInfo.Title, FallbackNoticeText, StringComparison.Ordinal))
        {
            VoiceInfo.IsOpen = false;
        }
    }

    /// <summary>The badge only ever shows these three labels; the model is the source state.</summary>
    private static string DescribeCaptureSource(AudioSourceState state) => state.Kind switch
    {
        AudioCaptureSourceKind.ProcessLoopback => "VRChat 音频",
        AudioCaptureSourceKind.SystemLoopbackFallback => "系统声音（兼容模式）",
        _ => "系统声音"
    };

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
        if (width > 0) ContentColumn.Width = Math.Min(900, Math.Max(1, width - 60));

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
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(VoiceStatusDotHost, 0);
            Grid.SetRow(VoiceStatusDotHost, 0);
            Grid.SetColumnSpan(VoiceStatusDotHost, 1);
            Grid.SetRowSpan(VoiceStatusDotHost, 1);
            Grid.SetColumn(VoiceStatusActions, 0);
            Grid.SetRow(VoiceStatusActions, 1);
            Grid.SetColumnSpan(VoiceStatusActions, 2);
            Grid.SetRowSpan(VoiceStatusActions, 1);
            Grid.SetColumn(VoiceStatusLine, 1);
            Grid.SetRow(VoiceStatusLine, 0);
            Grid.SetColumnSpan(VoiceStatusLine, 1);
            Grid.SetRowSpan(VoiceStatusLine, 1);
            Grid.SetColumn(VoiceHotkeySummary, 0);
            Grid.SetRow(VoiceHotkeySummary, 2);
            Grid.SetColumnSpan(VoiceHotkeySummary, 2);
            Grid.SetRowSpan(VoiceHotkeySummary, 1);
            VoiceHotkeySummary.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            VoiceStatusGrid.ColumnDefinitions.Clear();
            VoiceStatusGrid.RowDefinitions.Clear();
            // Auto first column: a fixed 18px column clipped the 40px icon
            // badge down to a sliver on the left edge of the card.
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            VoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Icon and status line span both rows so they share one centre
            // line; the button keeps the top row with the hotkey chip below it.
            Grid.SetColumn(VoiceStatusDotHost, 0);
            Grid.SetRow(VoiceStatusDotHost, 0);
            Grid.SetColumnSpan(VoiceStatusDotHost, 1);
            Grid.SetRowSpan(VoiceStatusDotHost, 2);
            Grid.SetColumn(VoiceStatusActions, 2);
            Grid.SetRow(VoiceStatusActions, 0);
            Grid.SetColumnSpan(VoiceStatusActions, 1);
            Grid.SetRowSpan(VoiceStatusActions, 1);
            Grid.SetColumn(VoiceStatusLine, 1);
            Grid.SetRow(VoiceStatusLine, 0);
            Grid.SetColumnSpan(VoiceStatusLine, 1);
            Grid.SetRowSpan(VoiceStatusLine, 2);
            Grid.SetColumn(VoiceHotkeySummary, 2);
            Grid.SetRow(VoiceHotkeySummary, 1);
            Grid.SetColumnSpan(VoiceHotkeySummary, 1);
            Grid.SetRowSpan(VoiceHotkeySummary, 1);
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
            ResetAudioLevel();
            UpdateRunningVisuals();
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs args)
    {
        // Capture callbacks arrive at whatever rate the device produces; the meter
        // samples them on its own display tick so the bars scroll evenly.
        _audioMeter.Observe(args.Rms);
        if (!_audioLevelTimer.IsRunning) _audioLevelTimer.Start();
    }

    private void AdvanceAudioLevel()
    {
        if (!IsRunning)
        {
            _audioLevelTimer.Stop();
            ResetAudioLevel();
            return;
        }

        _audioMeter.Advance();
        _levelBars?.Render(_audioMeter.History);
    }

    /// <summary>
    /// The meter spans whatever width the card has, so the bar count follows that
    /// width instead of leaving a gap next to the title.
    /// </summary>
    private void ResizeAudioLevel(double width)
    {
        if (_levelBars is null || width <= 0) return;
        const double pitch = 9;
        var count = (int)Math.Clamp(Math.Floor((width + 3) / pitch), 8, 64);
        if (count == _levelBars.BarCount) return;
        _levelBars.Resize(count, 6, 3);
        _audioMeter.Resize(count);
    }

    private void ResetAudioLevel()
    {
        _audioMeter.Reset();
        // The idle floor inside the renderer keeps the bars flat, not blank.
        _levelBars?.Reset();
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
        VoiceStatusText.Text = IsRunning ? "他人语音识别中" : "他人语音识别停止";
        VoiceStartButton.Content = IsRunning ? "停止" : "开始";
        AutomationProperties.SetName(VoiceStartButton, IsRunning ? "停止识别" : "开始识别");
        VoiceStatusBadge.Background = IsRunning
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 214, 240, 227))
            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 217, 233, 255));
        VoicePulse.IsActive = IsRunning;
        VoicePulse.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        // The badge names the source of the running capture; while stopped there
        // is nothing being captured, so it stays out of the way.
        UpdateCaptureSource(State.SubtitleVoice.SourceState, announceFallback: true);
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
        State.LocalSpeech.SpeakerLabelsEnabled = _settings.SpeakerLabels;
        SpeechModelBox.SelectedIndex = 0;
        VoiceHotkeyValue.Text = ReadGlobalVoiceHotkey();
        AutomationProperties.SetName(VoiceHotkeyValue, VoiceHotkeyValue.Text);
        UpdateSpeakerPanel();
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
        // Any other notice replaces the fallback hint, so a later recovery must
        // not close a message the user still needs to read.
        _fallbackNoticeActive = string.Equals(message, FallbackNoticeText, StringComparison.Ordinal);
        VoiceInfo.Title = message;
        VoiceInfo.Message = string.Empty;
        VoiceInfo.Severity = severity;
        VoiceInfo.IsOpen = true;
    }

    private static string ReadGlobalVoiceHotkey()
    {
        var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
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

    private void OnSpeakerToggleToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updatingSpeakerToggle) return;
        State.LocalSpeech.SpeakerLabelsEnabled = SpeakerToggle.IsOn;
        _settings.SpeakerLabels = SpeakerToggle.IsOn;
        SaveSettings();
        UpdateSpeakerPanel();
    }

    private void OnSpeakerSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSpeakerButtons();

    private async void OnRenameSpeakerClicked(object sender, RoutedEventArgs e)
    {
        if (SpeakerList.SelectedItem is not SpeakerIdentity speaker) return;
        var input = new TextBox
        {
            Text = speaker.Name ?? string.Empty,
            PlaceholderText = "玩家名，例如 小明",
            MaxLength = 24,
        };
        var dialog = new ContentDialog
        {
            Title = $"命名 {speaker.Label}",
            Content = input,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        State.LocalSpeech.Speakers?.Rename(speaker.Id, input.Text);
        UpdateSpeakerPanel();
        ShowInfo($"已命名为「{input.Text.Trim()}」，下次会话会自动沿用", InfoBarSeverity.Success);
    }

    private async void OnMergeSpeakerClicked(object sender, RoutedEventArgs e)
    {
        if (SpeakerList.SelectedItem is not SpeakerIdentity speaker) return;
        var targets = _speakers.Where(item => item.Id != speaker.Id).ToArray();
        if (targets.Length == 0)
        {
            ShowInfo("还没有第二个人可以合并", InfoBarSeverity.Warning);
            return;
        }

        var picker = new ComboBox
        {
            ItemsSource = targets,
            DisplayMemberPath = nameof(SpeakerIdentity.DisplayName),
            SelectedIndex = 0,
            MinWidth = 220,
        };
        var dialog = new ContentDialog
        {
            Title = $"把 {speaker.DisplayName} 合并到",
            Content = picker,
            PrimaryButtonText = "合并",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (picker.SelectedItem is not SpeakerIdentity target) return;

        State.LocalSpeech.Speakers?.Merge(speaker.Id, target.Id);
        UpdateSpeakerPanel();
    }

    private void OnForgetSpeakerClicked(object sender, RoutedEventArgs e)
    {
        if (SpeakerList.SelectedItem is not SpeakerIdentity speaker) return;
        State.LocalSpeech.Speakers?.Forget(speaker.Id);
        UpdateSpeakerPanel();
    }

    private async void OnClearSpeakersClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空声纹库",
            Content = new TextBlock
            {
                Text = "所有已命名的声纹都会从本机删除，无法恢复。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "清空",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        State.LocalSpeech.Speakers?.Clear();
        UpdateSpeakerPanel();
    }

    private void UpdateSpeakerButtons()
    {
        var selected = SpeakerList.SelectedItem is SpeakerIdentity;
        SpeakerRenameButton.IsEnabled = selected;
        SpeakerMergeButton.IsEnabled = selected;
        SpeakerForgetButton.IsEnabled = selected;
    }

    /// <summary>Mirrors the speaker library into the card; the feature is off by default.</summary>
    private void UpdateSpeakerPanel()
    {
        if (SpeakerList is null || SpeakerToggle is null || SpeakerStatus is null) return;
        var identifier = State.LocalSpeech.Speakers;
        var available = State.LocalSpeech.Speakers is { IsAvailable: true };
        _speakers = available ? identifier!.Speakers : [];

        SpeakerList.ItemsSource = _speakers;
        SpeakerToggle.IsEnabled = available;
        _updatingSpeakerToggle = true;
        try
        {
            SpeakerToggle.IsOn = available && State.LocalSpeech.SpeakerLabelsEnabled;
        }
        finally
        {
            _updatingSpeakerToggle = false;
        }

        SpeakerStatus.Text = !available
            ? "说话人模型不可用，重新导入本地组件后可恢复"
            : State.LocalSpeech.SpeakerLabelsEnabled
                ? _speakers.Count == 0
                    ? "已开启 · 等待第一位说话人"
                    : $"已开启 · 本会话识别到 {_speakers.Count} 位说话人，命名后会跨会话沿用"
                : "他人语音暂不区分说话人";
        UpdateSpeakerButtons();
    }

    private sealed class VoiceSettings
    {
        public string Provider { get; set; } = "local";

        public bool SpeakerLabels { get; set; }
    }

    private sealed class GlobalHotkeySettings
    {
        public string VoiceHotkey { get; set; } = "F7";
    }
}
