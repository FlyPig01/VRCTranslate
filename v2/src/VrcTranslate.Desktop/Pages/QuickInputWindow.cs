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
/// The lower strip is only an output preview and collapses when empty.
/// </summary>
public sealed class QuickInputWindow : Window
{
    private readonly AppState _state;
    private readonly TextBox _input;
    private readonly TextBlock _previewOutput;
    private readonly TextBlock _result;
    private readonly Grid _previewSurface;
    private readonly Grid _previewDivider;
    private readonly Grid _surface;
    private readonly EventHandler _previewHandler;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private OverlayWindowController? _windowController;
    private bool _sending;

    public QuickInputWindow(AppState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _input = new TextBox
        {
            PlaceholderText = "输入中文",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            MaxLength = 1000,
            MinHeight = 40,
            FontSize = 16,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Keep the editor transparent so the single overlay surface owns
            // the visual treatment.
            Background = Brush("#00000000"),
            Foreground = Brush("#FFFFFF"),
            BorderBrush = Brush("#00000000"),
            BorderThickness = new Thickness(0)
        };
        ConfigureInputVisuals(_input);
        AutomationProperties.SetName(_input, "待翻译文字");
        AutomationProperties.SetAutomationId(_input, "quick-input-text");
        _input.KeyDown += OnInputKeyDown;

        // Keep the recent result compact and unambiguous: one line contains
        // the same slash-separated text that is sent through OSC.
        _previewOutput = CreateOutputText(12, bold: false);
        _previewOutput.Margin = new Thickness(6, 4, 6, 4);
        AutomationProperties.SetName(_previewOutput, "最近翻译预览");
        AutomationProperties.SetAutomationId(_previewOutput, "quick-input-preview");
        _result = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("#F0A5A5"),
            Visibility = Visibility.Collapsed,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        _previewSurface = new Grid
        {
            // The preview is part of the same client surface. Keep it
            // transparent so a second card never appears inside the ordinary
            // window client area.
            Background = Brush("#18000000"),
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _previewSurface.Children.Add(_previewOutput);

        // A single hairline gives the editor and recent-result areas a clear
        // boundary without introducing another rounded card or frame.
        _previewDivider = new Grid
        {
            Height = 1,
            Background = Brush("#42FFFFFF"),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var inputRow = new Grid
        {
            ColumnSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.Children.Add(_input);
        inputRow.SizeChanged += (_, args) => ResizeInputEditor(args.NewSize.Height);

        // Keep the editor in a star-sized row. A StackPanel measures its
        // children at their desired height, so the old layout left a large
        // resized window with an unchanged input area. The grid lets the
        // editor consume every extra pixel while preview/status rows remain
        // compact and predictable.
        var body = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(inputRow, 0);
        Grid.SetRow(_previewDivider, 1);
        Grid.SetRow(_previewSurface, 2);
        Grid.SetRow(_result, 3);
        body.Children.Add(inputRow);
        body.Children.Add(_previewDivider);
        body.Children.Add(_previewSurface);
        body.Children.Add(_result);

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
        // Keep a compact inset within the standard Windows client area.
        body.Margin = new Thickness(8, 7, 8, 7);
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
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = Brush("#FFFFFF"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// Keeps the single-line editor and its contents centred while its grid
    /// region grows and shrinks with the native window. The stock WinUI
    /// TextBox template does not bind VerticalContentAlignment to its internal
    /// ScrollViewer or placeholder TextBlock, so responsive symmetric padding
    /// is required for both the caret text and the placeholder. When a preview
    /// appears, the star row supplies only the remaining editor height.
    /// </summary>
    private void ResizeInputEditor(double availableHeight)
    {
        if (double.IsNaN(availableHeight) || double.IsInfinity(availableHeight) || availableHeight <= 0)
        {
            return;
        }

        var editorHeight = Math.Max(40, availableHeight);
        var fontSize = Math.Clamp(editorHeight * 0.12, 16, 24);
        var lineHeight = fontSize * 1.5;
        var verticalPadding = Math.Max(0, (editorHeight - lineHeight) / 2);

        _input.Height = editorHeight;
        _input.FontSize = fontSize;
        _input.Padding = new Thickness(2, verticalPadding, 2, verticalPadding);
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

        input.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        input.UseSystemFocusVisuals = false;
        input.Padding = new Thickness(0);
    }

    private void EnsureWindowController()
    {
        if (_windowController is not null) return;
        try
        {
            _windowController = new OverlayWindowController(
                this,
                "输入",
                1240,
                180,
                OverlayWindowHost.GetSavedLayout(OverlayWindowHost.QuickInputLayoutKey),
                layout => OverlayWindowHost.SaveLayout(OverlayWindowHost.QuickInputLayoutKey, layout),
                _state.OverlayAppearance.Current.InputOverlayOpacity);
        }
        catch
        {
            // Native presentation is best effort; the editor remains usable.
        }

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
            ShowResult("请输入文字", false);
            return;
        }

        _sending = true;
        try
        {
            var result = await _state.TranslateSelfAsync(source, TextTranslationSource.ManualText);
            _state.SetTranslationPreview(
                source,
                result.Primary.TranslatedText,
                result.Secondary?.TranslatedText,
                result.Targets);
            await _state.Osc.SendChatboxAsync(TrimForChatbox(result.FormattedText));
            _input.Text = string.Empty;
            HideResult();
        }
        catch (Exception ex)
        {
            ShowResult($"发送失败：{ex.Message}", false);
        }
        finally
        {
            _sending = false;
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
        _previewOutput.Text = TranslationOutputFormatter.Format(
            original,
            translated,
            secondaryTranslated);
        var hasPreview = !string.IsNullOrWhiteSpace(_previewOutput.Text);
        _previewSurface.Visibility = !hasPreview
            ? Visibility.Collapsed
            : Visibility.Visible;
        _previewDivider.Visibility = hasPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowResult(string message, bool success)
    {
        _result.Text = message;
        _result.Foreground = Brush(success ? "#82D7B0" : "#F0A5A5");
        _result.Visibility = Visibility.Visible;
    }

    private void HideResult() => _result.Visibility = Visibility.Collapsed;

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
