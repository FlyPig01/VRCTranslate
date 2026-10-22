using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using VrcTranslate.Infrastructure.Storage;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Application.Subtitles;
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
    private bool _speakerEventsAttached;
    private bool _fallbackNoticeActive;
    private bool _selfTestAttached;

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
        if (!_selfTestAttached)
        {
            State.SpeechSelfTestChanged += OnSpeechSelfTestChanged;
            _selfTestAttached = true;
        }

        // Catch up in case the size settled before this load callback ran.
        ResizeAudioLevel(VoiceAudioLevelHost.ActualWidth);
        LoadSettings();
        UpdateLocalModelStatus();
        UpdateSpeechSelfTestStatus();
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
        if (!_speakerEventsAttached && State.LocalSpeech.Speakers is { } speakers)
        {
            speakers.SpeakersChanged += OnSpeakersChanged;
            _speakerEventsAttached = true;
        }
    }

    private void DetachSpeechEvents()
    {
        if (!_speechEventsAttached) return;
        _speechEventsAttached = false;
        State.SubtitleVoice.RunningChanged -= OnSessionRunningChanged;
        State.SubtitleVoice.Notified -= OnSessionNotified;
        State.SubtitleVoice.LevelChanged -= OnAudioLevelChanged;
        State.SubtitleVoice.SourceChanged -= OnCaptureSourceChanged;
        if (_speakerEventsAttached && State.LocalSpeech.Speakers is { } speakers)
        {
            speakers.SpeakersChanged -= OnSpeakersChanged;
            _speakerEventsAttached = false;
        }
    }

    private void OnSpeakersChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateSpeakerPanel);

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

    private void OnSpeechSelfTestChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            UpdateSpeechSelfTestStatus();
            UpdateLocalModelStatus();
        }

        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    /// <summary>
    /// The self-test answer belongs at the top of the page, before the user
    /// starts anything: a model that cannot run is the one case where starting
    /// captions only produces an empty window.
    /// </summary>
    private void UpdateSpeechSelfTestStatus()
    {
        if (SpeechSelfTestInfo is null) return;
        var result = State.SpeechSelfTest;
        if (result is null || result.IsUsable)
        {
            SpeechSelfTestInfo.IsOpen = false;
            return;
        }

        SpeechSelfTestInfo.Severity = InfoBarSeverity.Error;
        SpeechSelfTestInfo.Title = "本地语音自检失败";
        SpeechSelfTestInfo.Message =
            $"{result.Message}。字幕将无法生成；诊断日志：{CaptionDiagnostics.Path}";
        SpeechSelfTestInfo.IsOpen = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _overlayAppearanceReady = false;
        State.OverlayAppearance.Changed -= OnOverlayAppearanceChanged;
        if (_selfTestAttached)
        {
            State.SpeechSelfTestChanged -= OnSpeechSelfTestChanged;
            _selfTestAttached = false;
        }

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
        // The cached verdict describes the payload before the install/remove.
        // Re-run it so the red bar disappears as soon as the repair worked (or
        // explains the new state).
        _ = State.RefreshSpeechSelfTestAsync();
    }

    private void UpdateLocalModelStatus()
    {
        var status = State.LocalSpeech.GetModelStatus();
        var text = status.State switch
        {
            LocalSpeechModelState.Ready => status.IsBundled
                ? "已就绪（随程序内置）· 中 / 英 / 日 / 韩"
                : "已就绪 · 中 / 英 / 日 / 韩",
            LocalSpeechModelState.Installing => "正在安装…",
            LocalSpeechModelState.Invalid => "文件不完整，请重新安装",
            _ => "尚未安装"
        };
        if (status.State == LocalSpeechModelState.Ready)
        {
            text += State.SpeechSelfTest switch
            {
                null => " · 自检中…",
                { IsUsable: true } => " · 自检通过",
                _ => " · 自检失败"
            };
        }

        ModelStatus.Text = text;
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e) =>
        await SetRecognitionAsync(!IsRunning);

    /// <summary>
    /// Turns other-player recognition and its caption surface on or off through
    /// the one serialized master switch that the global shortcut also drives, so
    /// this button can never leave the window and the session disagreeing.
    /// </summary>
    private async Task SetRecognitionAsync(bool enabled)
    {
        try
        {
            // VRChat audio is received through the Windows loopback adapter;
            // segmentation, SenseVoice inference, translation and OSC output
            // stay in the application-scoped session host.
            var outcome = await State.OtherPlayerCaption.SetEnabledAsync(enabled);
            if (outcome == OtherPlayerCaptionToggleOutcome.ModelNotReady) ShowModelNotReadyHint();
        }
        catch (Exception exception)
        {
            ShowInfo("无法读取系统音频，请检查音频设备后重试。", InfoBarSeverity.Warning);
            _ = exception;
        }
        finally
        {
            if (!IsRunning)
            {
                _audioLevelTimer.Stop();
                ResetAudioLevel();
            }

            UpdateRunningVisuals();
        }
    }

    /// <summary>
    /// The one explicit notice a refused start earns, shared by the shortcut and
    /// this page: without it the model requirement looks like a dead key.
    /// </summary>
    public void ShowModelNotReadyHint()
    {
        ShowInfo("本地语音模型尚未就绪，请先安装模型", InfoBarSeverity.Warning);
        UpdateLocalModelStatus();
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
        State.LocalSpeech.SpeakerLabelsEnabled = true;
        ApplySubtitleContentSetting();
        SpeechModelBox.SelectedIndex = 0;
        HotkeyDisplay.Apply(VoiceHotkeyValue, ReadGlobalVoiceHotkey());
        LoadSpeechEngines();
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

    /// <summary>
    /// The 他人语音 shortcut as the global poller will answer it: an empty value
    /// is a deliberate 未设置, so the hint must not resurrect the default, while
    /// a missing, unreadable or unsupported value keeps the shipped default.
    /// </summary>
    private static string ReadGlobalVoiceHotkey() => HotkeyDisplay.ReadOtherPlayerVoice();

    /// <summary>
    /// Mirrors the persisted caption option into the switch and into the open
    /// caption surface. "译文 + 原文" is the shape earlier builds always used.
    /// </summary>
    private void ApplySubtitleContentSetting()
    {
        var mode = SubtitleCaptionSettings.FromTag(_settings.SubtitleContent);
        _settings.SubtitleContent = SubtitleCaptionSettings.ToTag(mode);
        SubtitleContentToggle.IsOn = mode == SubtitleContentMode.TranslatedWithOriginal;
        OverlayWindowHost.ApplySubtitleContentMode(mode);
    }

    private void OnSubtitleContentToggled(object sender, RoutedEventArgs e)
    {
        // The switch is set from the saved value while the page loads; that pass
        // must not write the file back.
        if (!_loaded) return;
        var mode = SubtitleContentToggle.IsOn
            ? SubtitleContentMode.TranslatedWithOriginal
            : SubtitleContentMode.TranslatedOnly;
        _settings.SubtitleContent = SubtitleCaptionSettings.ToTag(mode);
        SaveSettings();
        OverlayWindowHost.ApplySubtitleContentMode(mode);
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

        var namedCount = _speakers.Count(item => item.Name is not null);
        if (speaker.Name is null && namedCount >= 100)
        {
            ShowInfo("命名库已满（100 人），请先合并或忘记一个说话人", InfoBarSeverity.Warning);
            return;
        }

        var renamed = State.LocalSpeech.Speakers?.Rename(speaker.Id, input.Text) ?? false;
        if (!renamed)
        {
            ShowInfo("命名失败：命名库已满，请先合并或忘记一个说话人", InfoBarSeverity.Warning);
            return;
        }

        UpdateSpeakerPanel();
        if (speaker.ObservationCount < 2)
        {
            ShowInfo(
                $"已命名为「{input.Text.Trim()}」，但当前样本较少；建议让 TA 再说几句再使用",
                InfoBarSeverity.Warning);
        }
        else
        {
            ShowInfo($"已命名为「{input.Text.Trim()}」，下次会话会自动沿用", InfoBarSeverity.Success);
        }
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
        var selected = SpeakerList.SelectedItem as SpeakerIdentity;
        var namedCount = _speakers.Count(item => item.Name is not null);
        var atLibraryCap = selected?.Name is null && namedCount >= 100;
        SpeakerRenameButton.IsEnabled = selected is not null && !atLibraryCap;
        SpeakerMergeButton.IsEnabled = selected is not null;
        SpeakerForgetButton.IsEnabled = selected is not null;
    }

    /// <summary>Mirrors the speaker library into the card; labels are always on for other-player speech.</summary>
    private void UpdateSpeakerPanel()
    {
        if (SpeakerList is null || SpeakerStatus is null) return;
        var identifier = State.LocalSpeech.Speakers;
        var available = identifier is { IsAvailable: true };
        var selectedId = (SpeakerList.SelectedItem as SpeakerIdentity)?.Id;
        _speakers = available ? identifier!.Speakers : [];

        SpeakerList.ItemsSource = _speakers;
        if (selectedId is not null)
        {
            SpeakerList.SelectedItem = _speakers.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        }

        var namedCount = _speakers.Count(item => item.Name is not null);
        var sessionCount = _speakers.Count - namedCount;
        SpeakerStatus.Text = !available
            ? "说话人模型不可用，重新导入本地组件后可恢复"
            : _speakers.Count == 0
                ? "已常驻开启 · 等待第一位说话人"
                : $"已常驻开启 · 命名库 {namedCount}/100 · 本次会话 {sessionCount}/16";
        UpdateSpeakerButtons();
    }


    private enum SpeechEngine
    {
        Local,
        Qwen,
        TencentSentence
    }

    private SpeechEngine ActiveSpeechEngine()
    {
        var qwen = State.QwenAsr;
        if (qwen.Enabled && qwen.IsConfigured) return SpeechEngine.Qwen;
        var tencent = State.TencentAsr;
        if (tencent.Enabled && tencent.Mode == TencentAsrMode.Sentence && tencent.IsConfigured)
            return SpeechEngine.TencentSentence;
        return SpeechEngine.Local;
    }

    private void LoadSpeechEngines()
    {
        var qwen = State.QwenAsr;
        var tencent = State.TencentAsr;
        var active = ActiveSpeechEngine();
        SetSpeechCard(LocalSpeechCard, active == SpeechEngine.Local, LocalSpeechState);
        SetSpeechCard(QwenSpeechCard, active == SpeechEngine.Qwen, QwenSpeechState);
        SetSpeechCard(TencentSpeechCard, active == SpeechEngine.TencentSentence, TencentSpeechState);
        QwenSpeechState.Text = active == SpeechEngine.Qwen
            ? "当前使用"
            : qwen.IsConfigured ? "已配置" : "未配置";
        TencentSpeechState.Text = active == SpeechEngine.TencentSentence
            ? "当前使用"
            : tencent.IsConfigured ? "已配置" : "未配置";
    }

    private static void SetSpeechCard(Border card, bool current, TextBlock state)
    {
        card.BorderThickness = new Thickness(current ? 2 : 1);
        card.BorderBrush = new SolidColorBrush(current
            ? Microsoft.UI.ColorHelper.FromArgb(255, 72, 132, 224)
            : Microsoft.UI.ColorHelper.FromArgb(255, 215, 226, 240));
        card.Background = new SolidColorBrush(current
            ? Microsoft.UI.ColorHelper.FromArgb(255, 234, 242, 255)
            : Microsoft.UI.ColorHelper.FromArgb(255, 245, 248, 252));
        state.Text = current ? "当前使用" : string.Empty;
    }

    private void OnLocalSpeechCardTapped(object sender, TappedRoutedEventArgs e)
    {
        var qwen = State.QwenAsr.Clone();
        qwen.Enabled = false;
        State.SaveQwenAsr(qwen);
        var tencent = State.TencentAsr.Clone();
        tencent.Enabled = false;
        State.SaveTencentAsr(tencent);
        LoadSpeechEngines();
    }

    private void OnQwenSpeechCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!State.QwenAsr.IsConfigured)
        {
            _ = ShowQwenConfigAsync();
            return;
        }

        var qwen = State.QwenAsr.Clone();
        qwen.Enabled = true;
        State.SaveQwenAsr(qwen);
        var tencent = State.TencentAsr.Clone();
        tencent.Enabled = false;
        State.SaveTencentAsr(tencent);
        LoadSpeechEngines();
    }

    private void OnTencentSpeechCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!State.TencentAsr.IsConfigured)
        {
            _ = ShowTencentConfigAsync();
            return;
        }

        var tencent = State.TencentAsr.Clone();
        tencent.Mode = TencentAsrMode.Sentence;
        tencent.Enabled = true;
        State.SaveTencentAsr(tencent);
        var qwen = State.QwenAsr.Clone();
        qwen.Enabled = false;
        State.SaveQwenAsr(qwen);
        LoadSpeechEngines();
    }
    private async void OnConfigureQwenClicked(object sender, RoutedEventArgs e) =>
        await ShowQwenConfigAsync();

    private async Task ShowQwenConfigAsync()
    {
        var existing = State.QwenAsr;
        var apiKey = new PasswordBox
        {
            Header = "DashScope API Key",
            PlaceholderText = string.IsNullOrWhiteSpace(existing.ApiKey) ? "sk-..." : "留空保留已保存密钥",
            PasswordRevealMode = PasswordRevealMode.Peek,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var workspace = new TextBox
        {
            Header = "Workspace ID",
            Text = existing.WorkspaceId,
            PlaceholderText = "百炼业务空间 ID",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var region = new ComboBox { Header = "地域", HorizontalAlignment = HorizontalAlignment.Stretch };
        region.Items.Add(new ComboBoxItem { Content = "华北 2（北京）", Tag = "cn-beijing" });
        region.Items.Add(new ComboBoxItem { Content = "新加坡", Tag = "ap-southeast-1" });
        region.SelectedItem = region.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag?.ToString(), existing.Region)) ?? region.Items[0];

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(apiKey);
        panel.Children.Add(workspace);
        panel.Children.Add(region);
        panel.Children.Add(new TextBlock
        {
            Text = "模型：qwen3.8-livetranslate-flash-realtime · 输出简体中文字幕\n" +
                   "此模型自带翻译，不需要在翻译页配置翻译模型。\n" +
                   "北京地域新人 100 万 token（90 天，输入/输出共用），超出按 token 计费。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 112, 131)),
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = "阿里云实时翻译",
            Content = panel,
            PrimaryButtonText = "保存并测试",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var settings = State.QwenAsr.Clone();
        if (!string.IsNullOrWhiteSpace(apiKey.Password)) settings.ApiKey = apiKey.Password.Trim();
        settings.WorkspaceId = workspace.Text.Trim();
        settings.Region = (region.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cn-beijing";
        settings.Enabled = false;
        if (!settings.IsConfigured)
        {
            ShowSpeechInfo("请填写 API Key 和 Workspace ID。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            // Persist credentials, but route audio only after the handshake
            // succeeds; a failed test leaves the cloud card disabled.
            State.SaveQwenAsr(settings);
            QwenSpeechState.Text = "测试中…";
            await State.TestQwenAsrAsync(settings);
            settings.Enabled = true;
            State.SaveQwenAsr(settings);
            var tencent = State.TencentAsr.Clone();
            tencent.Enabled = false;
            State.SaveTencentAsr(tencent);
            ShowSpeechInfo("阿里云实时翻译连接成功。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            settings.Enabled = false;
            State.SaveQwenAsr(settings);
            ShowSpeechInfo("阿里云连接失败：" + exception.Message, InfoBarSeverity.Error);
        }

        LoadSpeechEngines();
    }

    private async void OnConfigureTencentClicked(object sender, RoutedEventArgs e) =>
        await ShowTencentConfigAsync();

    private async Task ShowTencentConfigAsync()
    {
        var existing = State.TencentAsr;
        var appId = new TextBox
        {
            Header = "AppID（纯数字）",
            Text = existing.AppId,
            PlaceholderText = "例如 1250000000",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var secretId = new TextBox
        {
            Header = "SecretId",
            Text = existing.SecretId,
            PlaceholderText = "AKID 开头",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var secretKey = new PasswordBox
        {
            Header = "SecretKey",
            PlaceholderText = string.IsNullOrWhiteSpace(existing.SecretKey) ? "填写 SecretKey" : "留空保留已保存密钥",
            PasswordRevealMode = PasswordRevealMode.Peek,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var language = new ComboBox { Header = "识别语言", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var engine in TencentAsrEngines.Selectable(TencentAsrMode.Sentence))
        {
            language.Items.Add(new ComboBoxItem
            {
                Content = TencentAsrEngines.DisplayName(engine),
                Tag = engine,
            });
        }
        language.SelectedItem = language.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag?.ToString(),
                TencentAsrEngines.Normalize(existing.Engine, TencentAsrMode.Sentence))) ?? language.Items[0];

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(appId);
        panel.Children.Add(secretId);
        panel.Children.Add(secretKey);
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock
        {
            Text = "一句话识别按句上传，非流式；字幕仍走翻译页配置的翻译服务。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 112, 131)),
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = "腾讯云按句识别",
            Content = panel,
            PrimaryButtonText = "保存并测试",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var settings = State.TencentAsr.Clone();
        settings.Mode = TencentAsrMode.Sentence;
        settings.Enabled = false;
        settings.AppId = appId.Text.Trim();
        settings.SecretId = secretId.Text.Trim();
        if (!string.IsNullOrWhiteSpace(secretKey.Password)) settings.SecretKey = secretKey.Password.Trim();
        settings.Engine = (language.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? TencentAsrEngines.Japanese;
        if (!settings.IsConfigured)
        {
            ShowSpeechInfo("请填写 AppID、SecretId 和 SecretKey。", InfoBarSeverity.Warning);
            return;
        }
        if (!settings.AppId.All(char.IsDigit))
        {
            ShowSpeechInfo("AppID 应为纯数字，不是 SecretId。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            State.SaveTencentAsr(settings);
            TencentSpeechState.Text = "测试中…";
            await State.TestTencentAsrAsync(settings);
            settings.Enabled = true;
            State.SaveTencentAsr(settings);
            var qwen = State.QwenAsr.Clone();
            qwen.Enabled = false;
            State.SaveQwenAsr(qwen);
            ShowSpeechInfo("腾讯云按句识别连接成功。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            settings.Enabled = false;
            State.SaveTencentAsr(settings);
            ShowSpeechInfo("腾讯云连接失败：" + exception.Message, InfoBarSeverity.Error);
        }

        LoadSpeechEngines();
    }

    private void ShowSpeechInfo(string message, InfoBarSeverity severity)
    {
        SpeechEngineInfo.Title = "语音识别";
        SpeechEngineInfo.Message = message;
        SpeechEngineInfo.Severity = severity;
        SpeechEngineInfo.IsOpen = true;
    }
    private sealed class VoiceSettings
    {
        public string Provider { get; set; } = "local";

        // Kept for reading older settings files; the feature is always on now.
        public bool SpeakerLabels { get; set; }

        /// <summary>
        /// Caption message content: "translated-only" or "translated-with-original".
        /// The overlay reads the same property out of v2-voice-settings.json.
        /// </summary>
        [JsonPropertyName(SubtitleCaptionSettings.ContentPropertyName)]
        public string SubtitleContent { get; set; } = SubtitleCaptionSettings.TranslatedWithOriginalTag;
    }
}
