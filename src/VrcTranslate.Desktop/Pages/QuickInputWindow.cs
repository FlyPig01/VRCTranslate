using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Low-profile game input overlay. Apart from the native “输入” caption it has
/// no explanatory labels or send button: type Chinese text and press Enter.
/// The editor keeps a fixed 46px strip at the top; everything below the hairline
/// is the single most recent result, which also carries the waiting, timeout and
/// error messages for the send that is currently in flight.
/// </summary>
public sealed class QuickInputWindow : Window
{
    /// <summary>等待译文的默认预算；超过后按超时处理并提示重试。</summary>
    public static TimeSpan DefaultTranslationTimeout { get; } = TimeSpan.FromSeconds(15);

    private const string BusyMessage = "翻译中…";
    private const string TimeoutMessage = "翻译超时，按 Enter 重试";
    private const string ErrorColor = "#FF9F7A";
    private const string NeutralColor = "#9FB0C7";
    /// <summary>聚焦下划线与强调色；深色浮窗上需要比正文更亮才看得出来。</summary>
    private const string AccentColor = "#4C9AFF";
    /// <summary>真正发往 OSC 的那份文本比译文主行再暗一档。</summary>
    private const string OscTextColor = "#7C8CA6";
    private const string PlaceholderColor = "#6B7C94";
    private const double InputFontSize = 18;
    private const double TranslationFontSize = 15;
    private const double OscFontSize = 11;
    private const double StatusFontSize = 12;
    private const string UiFontFamily = "Microsoft YaHei UI";
    /// <summary>
    /// 全新安装时的窗口高度（DIP）：16px 上边距 + 46px 输入区 + 1px 分隔线 +
    /// 两行结果 + 16px 下边距。与传给控制器的默认高度保持一致。
    /// </summary>
    private const int DefaultHeightDips = 150;

    private readonly AppState _state;
    private readonly TimeSpan _translationTimeout;
    private readonly TextBox _input;
    private readonly TextBlock _previewOutput;
    private readonly TextBlock _oscOutput;
    private readonly TextBlock _result;
    private readonly ProgressRing _progressRing;
    private readonly Grid _statusRow;
    private readonly StackPanel _resultArea;
    private readonly Grid _focusUnderline;
    private readonly Grid _previewDivider;
    private readonly Grid _surface;
    private readonly EventHandler _previewHandler;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private OverlayWindowController? _windowController;
    private bool _sending;

    public QuickInputWindow(AppState state)
        : this(state, DefaultTranslationTimeout)
    {
    }

    /// <summary>
    /// Same surface with an explicit wait budget. The 15-second default is
    /// injectable so a test host can shorten it instead of waiting it out.
    /// </summary>
    public QuickInputWindow(AppState state, TimeSpan translationTimeout)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        if (translationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(translationTimeout));
        }

        _translationTimeout = translationTimeout;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _input = new TextBox
        {
            PlaceholderText = "输入中文",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            MaxLength = 1000,
            MinHeight = 40,
            FontSize = InputFontSize,
            FontFamily = new FontFamily(UiFontFamily),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 上下对称留白让 18px 的正文在 44px 编辑行里垂直居中；左右不留内边距，
            // 编辑文字与下方结果行左对齐。
            Padding = new Thickness(0, 10, 0, 10),
            // Keep the editor transparent so the single overlay surface owns
            // the visual treatment.
            Background = Brush("#00000000"),
            Foreground = Brush("#FFFFFF"),
            PlaceholderForeground = Brush(PlaceholderColor),
            BorderBrush = Brush("#00000000"),
            BorderThickness = new Thickness(0)
        };
        ConfigureInputVisuals(_input);
        // 焦点只靠编辑行下方那条 2px 强调色下划线表达。
        _input.GotFocus += (_, _) => ShowFocusUnderline(true);
        _input.LostFocus += (_, _) => ShowFocusUnderline(false);
        AutomationProperties.SetName(_input, "待翻译文字");
        AutomationProperties.SetAutomationId(_input, "quick-input-text");
        _input.KeyDown += OnInputKeyDown;

        // 编辑行固定 46px 并贴在顶边：窗口被拉高时输入区不会跟着长高，多出来的
        // 高度全部留给下方的结果区，文字因此永远不会飘到窗口中间。
        var inputBlock = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        // 44px 编辑行 + 2px 聚焦下划线 = 约 46px 的固定输入区，宽度随窗口走。
        inputBlock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        inputBlock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        // 聚焦下划线：空出来的一行压在编辑行正下方，聚焦时才点亮。
        _focusUnderline = new Grid
        {
            Background = Brush("#00000000"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetName(_focusUnderline, "输入焦点下划线");
        Grid.SetRow(_input, 0);
        Grid.SetRow(_focusUnderline, 1);
        inputBlock.Children.Add(_input);
        inputBlock.Children.Add(_focusUnderline);

        // 结果区只保留最近一条：主行是译文，下面更暗的小字是真正发往 OSC 的那份文本。
        _previewOutput = CreateOutputText(TranslationFontSize, bold: false);
        AutomationProperties.SetName(_previewOutput, "最近翻译预览");
        AutomationProperties.SetAutomationId(_previewOutput, "quick-input-preview");
        _oscOutput = new TextBlock
        {
            FontSize = OscFontSize,
            FontFamily = new FontFamily(UiFontFamily),
            Foreground = Brush(OscTextColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_oscOutput, "VRChat 实发文本");

        _result = new TextBlock
        {
            FontSize = StatusFontSize,
            FontFamily = new FontFamily(UiFontFamily),
            Foreground = Brush(ErrorColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 等待态用中性灰的圆环，成功/失败/等待三种文案共用这一行，一律左对齐。
        _progressRing = new ProgressRing
        {
            IsActive = false,
            Width = 16,
            Height = 16,
            Foreground = Brush(NeutralColor),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            // 圆环收起时这条外边距也一起消失，状态文案因此与结果主行左对齐。
            Margin = new Thickness(0, 0, 8, 0)
        };
        _statusRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_progressRing, 0);
        Grid.SetColumn(_result, 1);
        _statusRow.Children.Add(_progressRing);
        _statusRow.Children.Add(_result);

        _resultArea = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            // 固定高度下这两行结果必须完整可见，所以结果区只留最小的上边距。
            Margin = new Thickness(0, 4, 0, 0)
        };
        _resultArea.Children.Add(_statusRow);
        _resultArea.Children.Add(_previewOutput);
        _resultArea.Children.Add(_oscOutput);

        // 输入区与结果区之间的一条低对比发丝线，始终存在。
        _previewDivider = new Grid
        {
            Height = 1,
            Background = Brush("#2A3B52"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 只有编辑行和分隔线是固定的，结果区拿走所有剩余高度并贴顶对齐。
        var body = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(inputBlock, 0);
        Grid.SetRow(_previewDivider, 1);
        Grid.SetRow(_resultArea, 2);
        body.Children.Add(inputBlock);
        body.Children.Add(_previewDivider);
        body.Children.Add(_resultArea);

        _surface = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            // The HWND supplies the adjustable transparency. Keeping XAML
            // opaque avoids pale seams and double-alpha text rendering.
            Background = Brush("#081321"),
            MinWidth = 300,
            MinHeight = 60,
            Opacity = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // 统一 16px 内边距：输入区因此正好距顶 16px。
        body.Margin = new Thickness(16);
        _surface.Children.Add(body);
        AutomationProperties.SetAutomationId(_surface, "quick-input-surface");

        Content = _surface;
        Title = "输入";
        ExtendsContentIntoTitleBar = false;

        SetPreview(
            _state.LastOriginalText,
            _state.LastTranslatedText,
            _state.LastSecondaryTranslatedText);
        _previewHandler = (_, _) =>
        {
            var original = state.LastOriginalText;
            var translated = state.LastTranslatedText;
            var secondary = state.LastSecondaryTranslatedText;
            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                SetPreview(original, translated, secondary);
            }
            else
            {
                _dispatcherQueue.TryEnqueue(() => SetPreview(original, translated, secondary));
            }
        };
        state.TranslationPreviewChanged += _previewHandler;
        Closed += (_, _) =>
        {
            _state.TranslationPreviewChanged -= _previewHandler;
        };
        EnsureWindowController();
    }

    private static TextBlock CreateOutputText(double fontSize, bool bold)
    {
        return new TextBlock
        {
            FontSize = fontSize,
            FontFamily = new FontFamily(UiFontFamily),
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = Brush("#FFFFFF"),
            // 结果区高度固定：主行只占一行，超长译文用省略号收起，完整文本仍在
            // 下面的 OSC 实发行和真正发出去的聊天框文本里。
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// WinUI's stock TextBox template replaces Background and BorderBrush in
    /// its PointerOver/Focused visual states.  Override those theme resources
    /// locally so focusing the editor never creates a second rectangle inside
    /// the ordinary window client area.
    /// </summary>
    private static void ConfigureInputVisuals(TextBox input)
    {
        var transparent = Brush("#00000000");
        foreach (var key in new[]
                 {
                     "TextControlBackground",
                     "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused",
                     "TextControlBackgroundDisabled",
                     "TextControlBorderBrush",
                     "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused",
                     "TextControlBorderBrushDisabled"
                 })
        {
            input.Resources[key] = transparent;
        }

        // 只读态（等待译文时）同样不得画出第二个矩形：焦点只由外部的 2px 下划线表达。
        foreach (var key in new[]
                 {
                     "TextControlBackgroundReadOnly",
                     "TextControlBorderBrushReadOnly"
                 })
        {
            input.Resources[key] = transparent;
        }

        input.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        input.UseSystemFocusVisuals = false;
    }

    private void ShowFocusUnderline(bool focused)
        => _focusUnderline.Background = Brush(focused ? AccentColor : "#00000000");

    private void EnsureWindowController()
    {
        if (_windowController is not null) return;
        try
        {
            var saved = OverlayWindowHost.GetSavedLayout(OverlayWindowHost.QuickInputLayoutKey);
            _windowController = new OverlayWindowController(
                this,
                "输入",
                1240,
                150,
                saved,
                layout => OverlayWindowHost.SaveLayout(OverlayWindowHost.QuickInputLayoutKey, layout),
                _state.OverlayAppearance.Current.InputOverlayOpacity);
            // 控制器拿到的默认尺寸是物理像素，而 150 是"输入行 + 分隔线 + 两行结果"
            // 的 DIP 高度；缩放不是 100% 时同一个数字会换算出更矮的窗口。全新安装
            // （没有保存过矩形）时在内容加载后按当前缩放补正一次，用户保存过的尺寸
            // 完全不动。
            if (saved is null)
            {
                _surface.Loaded += (_, _) => FitDefaultHeightToDisplayScale();
            }
        }
        catch
        {
            // Native presentation is best effort; the editor remains usable.
        }

    }

    /// <summary>
    /// 把全新安装的默认高度换算成当前显示缩放下的物理像素，保证 150 DIP 的内容
    /// （输入行、分隔线、两行结果）在高 DPI 屏幕上同样完整可见。
    /// </summary>
    private void FitDefaultHeightToDisplayScale()
    {
        var scale = _surface.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return;
        }

        _windowController?.ResizeKeepingTop((int)Math.Ceiling(DefaultHeightDips * scale));
    }

    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            HideOverlay();
        }
    }

    internal bool IsOverlayVisible =>
        _windowController?.IsVisible ?? OverlayWindowController.IsFallbackVisible(this);

    internal bool IsOverlayMinimized =>
        _windowController?.IsMinimized ?? OverlayWindowController.IsFallbackMinimized(this);

    internal void ShowOverlay(bool activate)
    {
        EnsureWindowController();
        if (_windowController is not null) _windowController.Show(activate);
        else OverlayWindowController.ShowFallback(this, activate);
        if (activate) _input.Focus(FocusState.Programmatic);
    }

    internal void HideOverlay()
    {
        if (_windowController is not null) _windowController.Hide();
        else OverlayWindowController.HideFallback(this);
    }

    internal void ApplyOpacity(double opacity) => _windowController?.ApplyOpacity(opacity);

    internal bool TryGetLayout(out VrcTranslate.Core.Settings.OverlayWindowLayout layout)
    {
        if (_windowController is not null) return _windowController.TryGetLayout(out layout);
        layout = default;
        return false;
    }

    internal void ClosePermanently()
    {
        if (_windowController is not null) _windowController.ClosePermanently();
        else Close();
    }

    private async Task SendAsync()
    {
        if (_sending) return;
        var source = _input.Text.Trim();
        if (source.Length == 0)
        {
            ShowStatus("请输入文字", ErrorColor);
            return;
        }

        _sending = true;
        SetBusy(true);
        // 等待预算由窗口持有：超时后取消底层请求，UI 立刻回到可重试状态。
        using var timeout = new CancellationTokenSource(_translationTimeout);
        try
        {
            var result = await _state.TranslateSelfAsync(
                source,
                TextTranslationSource.ManualText,
                timeout.Token);
            // 结果区主行是译文，小字是真正通过 OSC 发出去的那一份文本。
            var oscText = TrimForChatbox(result.FormattedText);
            _state.SetTranslationPreview(
                source,
                result.Primary.TranslatedText,
                result.Secondary?.TranslatedText,
                result.Targets);
            await _state.Osc.SendChatboxAsync(oscText);
            ShowTranslation(result.Primary.TranslatedText, oscText);
            _input.Text = string.Empty;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ShowStatus(TimeoutMessage, ErrorColor);
        }
        catch (TimeoutException)
        {
            // 翻译服务自己的超时和这里的预算对用户是同一件事。
            ShowStatus(TimeoutMessage, ErrorColor);
        }
        catch (Exception ex)
        {
            ShowStatus($"发送失败：{ex.Message} · 按 Enter 重试", ErrorColor);
        }
        finally
        {
            SetBusy(false);
            _sending = false;
        }
    }

    /// <summary>
    /// 等待译文期间的状态：结果区立刻显示「翻译中…」和中性灰圆环，编辑框保留
    /// 内容但变暗且只读。只读加上 <c>_sending</c> 让重复 Enter 不会二次提交，
    /// 失败后文字仍在原处，用户直接重试即可。
    /// </summary>
    private void SetBusy(bool busy)
    {
        _input.IsReadOnly = busy;
        _input.Opacity = busy ? 0.45 : 1;
        if (busy)
        {
            ShowStatus(BusyMessage, NeutralColor);
            _progressRing.Visibility = Visibility.Visible;
            _progressRing.IsActive = true;
        }
        else
        {
            _progressRing.IsActive = false;
            _progressRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Updates the single-output preview used by the existing route.</summary>
    public void SetPreview(string original, string translated)
    {
        SetPreview(original, translated, string.Empty);
    }

    /// <summary>Updates the preview with a primary and optional second translation.</summary>
    public void SetPreview(string original, string translated, string? secondaryTranslated)
    {
        // 发送期间结果区归「翻译中…」所有；这次发送结束时会自己写入最新结果。
        if (_sending) return;
        var oscText = TrimForChatbox(TranslationOutputFormatter.Format(
            original,
            translated,
            secondaryTranslated));
        // 主行是译文本身；没有译文时退回这一条消息的完整内容，保证结果区不空。
        ShowTranslation(
            string.IsNullOrWhiteSpace(translated) ? oscText : translated,
            oscText);
    }

    /// <summary>
    /// Shows the most recent result: the primary translation on the main line
    /// and the exact OSC payload underneath in a dimmer, smaller line.
    /// </summary>
    private void ShowTranslation(string primary, string oscText)
    {
        var headline = primary?.Trim() ?? string.Empty;
        var payload = oscText?.Trim() ?? string.Empty;
        _previewOutput.Text = headline;
        _previewOutput.Visibility = headline.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        _oscOutput.Text = payload;
        _oscOutput.Visibility = payload.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        // 译文出现时状态文案让位：结果区一次只有一条内容。
        HideStatus();
    }

    private void ShowStatus(string message, string color)
    {
        _result.Text = message;
        _result.Foreground = Brush(color);
        _progressRing.IsActive = false;
        _progressRing.Visibility = Visibility.Collapsed;
        _statusRow.Visibility = Visibility.Visible;
        // 上一次的译文先收起，固定高度的结果区才不会被三条内容挤扁。
        _previewOutput.Visibility = Visibility.Collapsed;
        _oscOutput.Visibility = Visibility.Collapsed;
    }

    private void HideStatus()
    {
        _result.Text = string.Empty;
        _progressRing.IsActive = false;
        _progressRing.Visibility = Visibility.Collapsed;
        _statusRow.Visibility = Visibility.Collapsed;
    }

    private static SolidColorBrush Brush(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            throw new ArgumentException("Color must be RRGGBB or AARRGGBB.", nameof(value));
        }
        var offset = hex.Length == 8 ? 2 : 0;
        var a = offset == 2 ? Convert.ToByte(hex[..2], 16) : (byte)255;
        var r = Convert.ToByte(hex.Substring(offset, 2), 16);
        var g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
        var b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
        return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));
    }

    private static string TrimForChatbox(string text)
        => TranslationOutputFormatter.TrimForOsc(text);
}
