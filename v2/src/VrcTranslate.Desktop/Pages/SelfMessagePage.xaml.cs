using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Desktop.Pages;

/// <summary>Controls the user's microphone translation and shows the latest output.</summary>
public sealed partial class SelfMessagePage : Page
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCTranslate", "v2-self-voice-settings.json");
    private SelfVoiceSettings _settings = new();
    private bool _loaded;
    private bool _recognizing;
    private bool _processingResult;
    private bool _loadingTargets;
    private LocalSpeechCaptureSession? _captureSession;
    private readonly SemaphoreSlim _captureLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _microphoneLevelTimer;
    private double _measuredMicrophoneLevel;
    private DateTimeOffset _lastAudioFrameAt;
    private AppState State => ((App)global::Microsoft.UI.Xaml.Application.Current).State;

    public SelfMessagePage()
    {
        InitializeComponent();
        _microphoneLevelTimer = DispatcherQueue.CreateTimer();
        _microphoneLevelTimer.Interval = TimeSpan.FromMilliseconds(90);
        _microphoneLevelTimer.Tick += (_, _) => AnimateMicrophoneLevel();
        Loaded += (_, _) => LoadSettings();
        Loaded += OnPreviewLoaded;
        Unloaded += OnPreviewUnloaded;
        Loaded += (_, _) => QueueResponsiveLayout();
        SelfVoiceStatusGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        SelfVoiceDetailGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        VoiceInputGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
        TargetLanguageGrid.SizeChanged += (_, _) => QueueResponsiveLayout();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        if (e.NewSize.Width > 0)
            ContentColumn.Width = Math.Min(900, Math.Max(1, e.NewSize.Width - 60));
        QueueResponsiveLayout();
    }

    private void QueueResponsiveLayout()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            LayoutStatusPanel(SelfVoiceStatusGrid?.ActualWidth ?? 0);
            LayoutDetailGrid(SelfVoiceDetailGrid?.ActualWidth ?? 0);
            LayoutVoiceInput(VoiceInputGrid?.ActualWidth ?? 0);
            LayoutTargetLanguages(TargetLanguageGrid?.ActualWidth ?? 0);
        });
    }

    private void LayoutStatusPanel(double width)
    {
        if (SelfVoiceStatusGrid is null || width <= 0) return;
        SelfVoiceStatusGrid.ColumnDefinitions.Clear();
        SelfVoiceStatusGrid.RowDefinitions.Clear();

        if (width >= 650)
        {
            SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SelfVoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SelfVoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PlaceStatusElements(0, 1, 2, 0, 0, 0);
            Grid.SetColumn(MicrophoneLevel, 1);
            Grid.SetColumnSpan(MicrophoneLevel, 1);
            Grid.SetRow(MicrophoneLevel, 1);
            return;
        }

        // On a narrow shell, keep status text readable and place the controls
        // below it. This prevents the circular action button from being cut
        // off when the sidebar leaves only a small content column.
        SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SelfVoiceStatusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SelfVoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SelfVoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SelfVoiceStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(SelfVoiceIconHost, 0);
        Grid.SetRow(SelfVoiceIconHost, 0);
        Grid.SetColumnSpan(SelfVoiceIconHost, 1);
        Grid.SetColumn(SelfVoiceTextHost, 1);
        Grid.SetRow(SelfVoiceTextHost, 0);
        Grid.SetColumnSpan(SelfVoiceTextHost, 2);
        Grid.SetColumn(SelfVoiceStartButton, 2);
        Grid.SetRow(SelfVoiceStartButton, 1);
        Grid.SetColumn(MicrophoneLevel, 1);
        Grid.SetColumnSpan(MicrophoneLevel, 2);
        Grid.SetRow(MicrophoneLevel, 2);
    }

    private void PlaceStatusElements(int iconColumn, int textColumn, int buttonColumn, int iconRow, int textRow, int buttonRow)
    {
        Grid.SetColumn(SelfVoiceIconHost, iconColumn);
        Grid.SetRow(SelfVoiceIconHost, iconRow);
        Grid.SetColumnSpan(SelfVoiceIconHost, 1);
        Grid.SetColumn(SelfVoiceTextHost, textColumn);
        Grid.SetRow(SelfVoiceTextHost, textRow);
        Grid.SetColumnSpan(SelfVoiceTextHost, 1);
        Grid.SetColumn(SelfVoiceStartButton, buttonColumn);
        Grid.SetRow(SelfVoiceStartButton, buttonRow);
        Grid.SetColumn(MicrophoneLevel, textColumn);
        Grid.SetColumnSpan(MicrophoneLevel, 1);
        Grid.SetRow(MicrophoneLevel, 1);
    }

    private void LayoutDetailGrid(double width)
    {
        if (SelfVoiceDetailGrid is null || width <= 0) return;
        SelfVoiceDetailGrid.ColumnDefinitions.Clear();
        SelfVoiceDetailGrid.RowDefinitions.Clear();
        if (width >= 680)
        {
            SelfVoiceDetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            SelfVoiceDetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
            SelfVoiceDetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)SelfVoiceDetailGrid.Children[0], 0);
            Grid.SetRow((FrameworkElement)SelfVoiceDetailGrid.Children[0], 0);
            Grid.SetColumn((FrameworkElement)SelfVoiceDetailGrid.Children[1], 1);
            Grid.SetRow((FrameworkElement)SelfVoiceDetailGrid.Children[1], 0);
        }
        else
        {
            SelfVoiceDetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SelfVoiceDetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SelfVoiceDetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)SelfVoiceDetailGrid.Children[0], 0);
            Grid.SetRow((FrameworkElement)SelfVoiceDetailGrid.Children[0], 0);
            Grid.SetColumn((FrameworkElement)SelfVoiceDetailGrid.Children[1], 0);
            Grid.SetRow((FrameworkElement)SelfVoiceDetailGrid.Children[1], 1);
        }
    }

    private void LayoutVoiceInput(double width)
    {
        if (VoiceInputGrid is null || width <= 0) return;
        VoiceInputGrid.ColumnDefinitions.Clear();
        VoiceInputGrid.RowDefinitions.Clear();
        VoiceInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        VoiceInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        VoiceInputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(MicrophoneBox, 0);
        Grid.SetRow(MicrophoneBox, 0);
        Grid.SetColumn(MicrophoneTestButton, 1);
        Grid.SetRow(MicrophoneTestButton, 0);
        MicrophoneBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        MicrophoneBox.MinWidth = 0;
    }

    private void LayoutTargetLanguages(double width)
    {
        if (TargetLanguageGrid is null || width <= 0) return;
        TargetLanguageGrid.ColumnDefinitions.Clear();
        TargetLanguageGrid.RowDefinitions.Clear();
        if (width >= 520)
        {
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TargetLanguageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)TargetLanguageGrid.Children[0], 0);
            Grid.SetRow((FrameworkElement)TargetLanguageGrid.Children[0], 0);
            Grid.SetColumn(PrimaryTargetBox, 1);
            Grid.SetRow(PrimaryTargetBox, 0);
            Grid.SetColumn((FrameworkElement)TargetLanguageGrid.Children[2], 2);
            Grid.SetRow((FrameworkElement)TargetLanguageGrid.Children[2], 0);
            Grid.SetColumn(SecondaryTargetBox, 3);
            Grid.SetRow(SecondaryTargetBox, 0);
        }
        else
        {
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TargetLanguageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TargetLanguageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TargetLanguageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)TargetLanguageGrid.Children[0], 0);
            Grid.SetRow((FrameworkElement)TargetLanguageGrid.Children[0], 0);
            Grid.SetColumn(PrimaryTargetBox, 1);
            Grid.SetRow(PrimaryTargetBox, 0);
            Grid.SetColumn((FrameworkElement)TargetLanguageGrid.Children[2], 0);
            Grid.SetRow((FrameworkElement)TargetLanguageGrid.Children[2], 1);
            Grid.SetColumn(SecondaryTargetBox, 1);
            Grid.SetRow(SecondaryTargetBox, 1);
        }
        PrimaryTargetBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        SecondaryTargetBox.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        State.TranslationPreviewChanged += OnTranslationPreviewChanged;
        State.SelfTranslationTargetsChanged += OnSelfTranslationTargetsChanged;
        UpdateTranslationPreview();
        SelectTargetControls(State.SelfTranslationTargets);
    }

    private void OnPreviewUnloaded(object sender, RoutedEventArgs e)
    {
        State.TranslationPreviewChanged -= OnTranslationPreviewChanged;
        State.SelfTranslationTargetsChanged -= OnSelfTranslationTargetsChanged;
        _ = StopSelfVoiceAsync();
    }

    private void OnSelfTranslationTargetsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) SelectTargetControls(State.SelfTranslationTargets);
        else DispatcherQueue.TryEnqueue(() => SelectTargetControls(State.SelfTranslationTargets));
    }

    private void OnTranslationPreviewChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) UpdateTranslationPreview();
        else DispatcherQueue.TryEnqueue(UpdateTranslationPreview);
    }

    private void UpdateTranslationPreview()
    {
        RecentOriginal.Text = string.IsNullOrWhiteSpace(State.LastOriginalText) ? "暂无内容" : State.LastOriginalText;
        RecentTranslation.Text = string.IsNullOrWhiteSpace(State.LastTranslatedText) ? "暂无内容" : State.LastTranslatedText;
        var secondary = State.LastSecondaryTranslatedText;
        RecentSecondaryTranslation.Text = string.IsNullOrWhiteSpace(secondary) ? "暂无内容" : secondary;
        RecentSecondaryRow.Visibility = string.IsNullOrWhiteSpace(secondary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecentTranslationHeader.Text = FormatLanguage(State.LastPrimaryTargetLanguage);
        RecentSecondaryLabel.Text = FormatLanguage(State.LastSecondaryTargetLanguage);
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<SelfVoiceSettings>(File.ReadAllText(_settingsPath)) ?? new SelfVoiceSettings();
        }
        catch { _settings = new SelfVoiceSettings(); }

        SelfVoiceHotkeySummary.Text = ReadGlobalSelfVoiceHotkey();
        SelectByTag(MicrophoneBox, _settings.MicrophoneId);
        // Own voice is always recognized as Simplified Chinese. Keep the
        // persisted field for backward compatibility, but never expose a
        // second language selector in this page.
        _settings.SourceLanguage = "zh-CN";
        SelectTargetControls(State.SelfTranslationTargets);
        UpdateSelfVoiceVisuals();
        _loaded = true;
    }

    private void OnSelfVoiceStartClicked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (_recognizing) _ = StopSelfVoiceAsync();
        else _ = StartSelfVoiceAsync();
    }

    private void OnSelfVoiceSettingChanged(object sender, object e)
    {
        if (!_loaded) return;
        _settings.MicrophoneId = SelectedTag(MicrophoneBox, "default");
        _settings.SourceLanguage = "zh-CN";
        SaveSettings();
    }

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _loadingTargets) return;
        var primary = SelectedTag(PrimaryTargetBox, "en-US");
        var secondaryTag = SelectedTag(SecondaryTargetBox, "none");
        var secondary = string.Equals(secondaryTag, "none", StringComparison.OrdinalIgnoreCase)
            ? null
            : secondaryTag;
        if (secondary is not null && string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase))
        {
            // Keep the second selector useful when the user picks the same
            // language twice: reset it to the explicit off state.
            SelectByTag(SecondaryTargetBox, "none");
            secondary = null;
        }

        try
        {
            State.SetSelfTranslationTargets(primary, secondary);
            UpdateTranslationPreview();
        }
        catch (ArgumentException)
        {
            SelectTargetControls(State.SelfTranslationTargets);
        }
    }

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (Parent is Frame frame)
        {
            frame.Navigate(typeof(SettingsPage));
        }
    }

    private void OnOpenInputWindowClicked(object sender, RoutedEventArgs e)
    {
        var quickInputWindow = OverlayWindowHost.EnsureQuickInput(State);
        quickInputWindow.SetPreview(
            State.LastOriginalText,
            State.LastTranslatedText,
            State.LastSecondaryTranslatedText);
        OverlayWindowChrome.Show(quickInputWindow, activate: true);
    }

    public void ToggleSelfVoiceFromHotkey()
    {
        if (!_loaded) return;
        if (_recognizing) _ = StopSelfVoiceAsync();
        else _ = StartSelfVoiceAsync();
    }

    private async Task StartSelfVoiceAsync()
    {
        await _captureLifecycleGate.WaitAsync();
        try
        {
            if (_recognizing && _captureSession?.IsStarted == true) return;

            var modelStatus = State.LocalSpeech.GetModelStatus();
            if (modelStatus.State != LocalSpeechModelState.Ready)
            {
                _recognizing = false;
                ShowInfo("本地语音模型未安装", "请先在字幕页安装本地语音模型。", InfoBarSeverity.Warning);
                UpdateSelfVoiceVisuals();
                return;
            }

            // Capture and segmentation stay behind application interfaces. The
            // page only owns the session lifetime and consumes recognized text.
            var session = new LocalSpeechCaptureSession(
                State.AudioCapture.Create(AudioCaptureMode.Microphone, _settings.MicrophoneId),
                State.LocalSpeech,
                sourceLanguage: "zh-CN");
            session.ResultReady += OnSelfCaptureResult;
            session.Faulted += OnSelfCaptureFaulted;
            session.LevelChanged += OnSelfAudioLevelChanged;
            try
            {
                await session.StartAsync();
                _captureSession = session;
                _recognizing = true;
                _settings.Enabled = true;
                SaveSettings();
                _microphoneLevelTimer.Start();
                UpdateSelfVoiceVisuals();
            }
            catch
            {
                session.ResultReady -= OnSelfCaptureResult;
                session.Faulted -= OnSelfCaptureFaulted;
                session.LevelChanged -= OnSelfAudioLevelChanged;
                await session.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception)
        {
            _recognizing = false;
            _microphoneLevelTimer.Stop();
            UpdateSelfVoiceVisuals();
            ShowInfo("无法开始自身语音", "请检查麦克风权限和设备连接。", InfoBarSeverity.Warning);
            _ = exception;
        }
        finally
        {
            _captureLifecycleGate.Release();
        }
    }

    private async Task StopSelfVoiceAsync()
    {
        await _captureLifecycleGate.WaitAsync();
        try
        {
            _recognizing = false;
            _microphoneLevelTimer.Stop();
            MicrophoneLevel.Value = 0;
            _measuredMicrophoneLevel = 0;
            var session = _captureSession;
            _captureSession = null;
            if (session is not null)
            {
                session.ResultReady -= OnSelfCaptureResult;
                session.Faulted -= OnSelfCaptureFaulted;
                session.LevelChanged -= OnSelfAudioLevelChanged;
                await session.DisposeAsync();
            }
            _settings.Enabled = false;
            SaveSettings();
            UpdateSelfVoiceVisuals();
        }
        finally
        {
            _captureLifecycleGate.Release();
        }
    }

    private void OnSelfCaptureResult(object? sender, SpeechRecognitionResult result)
    {
        if (!_recognizing || string.IsNullOrWhiteSpace(result.Text)) return;
        DispatcherQueue.TryEnqueue(() => _ = ProcessSelfVoiceResultAsync(result.Text));
    }

    private void OnSelfCaptureFaulted(object? sender, Exception exception)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_recognizing)
                ShowInfo("语音采集已停止", "麦克风暂时不可用，请检查设备后重试。", InfoBarSeverity.Warning);
            _ = exception;
        });
    }

    /// <summary>Feeds one captured 16 kHz sentence into the local model.</summary>
    public async Task RecognizeSelfVoiceSamplesAsync(
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken = default)
    {
        if (!_recognizing || _processingResult) return;
        _processingResult = true;
        try
        {
            var result = await State.LocalSpeech.RecognizeAsync(
                new SpeechRecognitionRequest(samples, 16_000, _settings.SourceLanguage),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                await ProcessSelfVoiceResultAsync(result.Text);
            }
        }
        finally
        {
            _processingResult = false;
        }
    }

    private async Task ProcessSelfVoiceResultAsync(string original)
    {
        await _translationGate.WaitAsync();
        try
        {
            var result = await State.TranslateSelfAsync(original, TextTranslationSource.SpeechRecognition);
            State.SetTranslationPreview(
                original,
                result.Primary.TranslatedText,
                result.Secondary?.TranslatedText,
                result.Targets);
            await State.Osc.SendChatboxAsync(TranslationOutputFormatter.FormatForOsc(result));
            DispatcherQueue.TryEnqueue(() => ShowInfo("已发送", "自身语音译文已发送到 VRChat。", InfoBarSeverity.Success));
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() => ShowInfo("发送失败", exception.Message, InfoBarSeverity.Error));
        }
        finally
        {
            _processingResult = false;
            _translationGate.Release();
        }
    }

    private async void OnMicrophoneTestClicked(object sender, RoutedEventArgs e)
    {
        MicrophoneTestButton.IsEnabled = false;
        MicrophoneTestButton.Content = "检测中…";
        IAudioCapture? capture = null;
        var received = new TaskCompletionSource<AudioSamplesEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            capture = State.AudioCapture.Create(AudioCaptureMode.Microphone, _settings.MicrophoneId);
            void OnSamples(object? _, AudioSamplesEventArgs args) => received.TrySetResult(args);
            capture.SamplesReady += OnSamples;
            await capture.StartAsync();
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            capture.SamplesReady -= OnSamples;
            if (completed == received.Task)
            {
                var args = await received.Task;
                OnSelfAudioLevelChanged(this, new AudioLevelEventArgs(
                    MeasureRms(args.Samples.Span), MeasurePeak(args.Samples.Span)));
                ShowInfo("麦克风可用", "已收到声音。", InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo("没有收到声音", "麦克风已打开，请检查是否静音或选择了正确设备。", InfoBarSeverity.Warning);
                MicrophoneLevel.Value = 0;
            }
        }
        catch
        {
            ShowInfo("无法打开麦克风", "请检查系统权限和设备连接。", InfoBarSeverity.Warning);
            MicrophoneLevel.Value = 0;
        }
        finally
        {
            if (capture is not null)
            {
                try { await capture.StopAsync(); } catch { }
                await capture.DisposeAsync();
            }
            MicrophoneTestButton.Content = "检测声音";
            MicrophoneTestButton.IsEnabled = true;
        }
    }

    private void OnSelfAudioLevelChanged(object? sender, AudioLevelEventArgs args)
    {
        _lastAudioFrameAt = DateTimeOffset.UtcNow;
        var normalized = Math.Clamp((args.Rms * 280f) + (args.Peak * 20f), 0f, 100f);
        _measuredMicrophoneLevel = Math.Max(_measuredMicrophoneLevel * 0.35, normalized);
        if (DispatcherQueue.HasThreadAccess)
            MicrophoneLevel.Value = _measuredMicrophoneLevel;
        else
            DispatcherQueue.TryEnqueue(() => MicrophoneLevel.Value = _measuredMicrophoneLevel);
        if (!_microphoneLevelTimer.IsRunning) _microphoneLevelTimer.Start();
    }

    private void UpdateSelfVoiceVisuals()
    {
        var active = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 33, 165, 116));
        var inactive = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 154, 168, 184));
        SelfVoiceStatusText.Text = _recognizing ? "自身语音识别中" : "自身语音已停止";
        SelfVoiceStatusDot.Fill = _recognizing ? active : inactive;
        SelfVoiceStatusPanel.Background = new SolidColorBrush(_recognizing
            ? Microsoft.UI.ColorHelper.FromArgb(255, 235, 248, 241)
            : Microsoft.UI.ColorHelper.FromArgb(255, 247, 249, 252));
        SelfVoicePulse.IsActive = _recognizing;
        SelfVoiceStartButton.Content = _recognizing ? "停止" : "开始";
        AutomationProperties.SetName(SelfVoiceStartButton, _recognizing ? "停止自身语音" : "开始自身语音");
        SelfVoiceStartButton.Background = new SolidColorBrush(_recognizing
            ? Microsoft.UI.ColorHelper.FromArgb(255, 118, 86, 181)
            : Microsoft.UI.ColorHelper.FromArgb(255, 36, 122, 152));
    }

    private void AnimateMicrophoneLevel()
    {
        if (!_recognizing)
        {
            _microphoneLevelTimer.Stop();
            MicrophoneLevel.Value = 0;
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _lastAudioFrameAt;
        if (elapsed > TimeSpan.FromMilliseconds(220))
        {
            _measuredMicrophoneLevel *= 0.72;
            MicrophoneLevel.Value = _measuredMicrophoneLevel;
        }
        if (_measuredMicrophoneLevel < 0.5 && elapsed > TimeSpan.FromSeconds(1))
            _microphoneLevelTimer.Stop();
    }

    private static float MeasureRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        var sum = 0d;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float MeasurePeak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    private static string TrimUtf16(string value, int maxUnits)
    {
        if (value.Length <= maxUnits) return value;
        var length = maxUnits;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        SelfVoiceInfo.Title = title;
        SelfVoiceInfo.Message = message;
        SelfVoiceInfo.Severity = severity;
        SelfVoiceInfo.IsOpen = true;
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string SelectedTag(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void SelectTargetControls(TranslationTargetSet targets)
    {
        _loadingTargets = true;
        try
        {
            SelectByTag(PrimaryTargetBox, targets.PrimaryLanguage);
            SelectByTag(SecondaryTargetBox, targets.SecondaryLanguage ?? "none");
            RecentTranslationHeader.Text = FormatLanguage(targets.PrimaryLanguage);
            RecentSecondaryLabel.Text = FormatLanguage(targets.SecondaryLanguage);
            RecentSecondaryRow.Visibility = targets.SecondaryLanguage is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        finally
        {
            _loadingTargets = false;
        }
    }

    private static string FormatLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "en" or "en-us" or "en-gb" => "English",
        "ja" or "ja-jp" => "日本語",
        "ko" or "ko-kr" => "한국어",
        "zh" or "zh-cn" => "简体中文",
        _ => string.IsNullOrWhiteSpace(language) ? "译文 2" : language
    };

    private static string ReadGlobalSelfVoiceHotkey()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTranslate", "v2-user-settings.json");
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<GlobalHotkeySettings>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(settings?.SelfVoiceHotkey)) return settings.SelfVoiceHotkey.Trim();
            }
        }
        catch { }
        return "Ctrl+F8";
    }

    private static void SelectByTag(ComboBox box, string value)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (item is not null) box.SelectedItem = item;
    }

    private sealed class SelfVoiceSettings
    {
        public bool Enabled { get; set; }
        public string MicrophoneId { get; set; } = "default";
        public string SourceLanguage { get; set; } = "zh-CN";
        public string ToggleHotkey { get; set; } = "Ctrl+F8";
    }

    private sealed class GlobalHotkeySettings
    {
        public string SelfVoiceHotkey { get; set; } = "Ctrl+F8";
    }
}
