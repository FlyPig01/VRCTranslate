using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using VrcTranslate.Application.Subtitles;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Compact caption surface for other players' voices. The output language is
/// fixed to Simplified Chinese and the surface has no visible configuration and
/// no activity indicator: activity shows up as the messages that arrive, while
/// the level meter on the voice page owns input feedback. Recognized sentences
/// accumulate as chat-style messages that exist for this run only - nothing is
/// written to disk and there is no clear action. Stopping recognition takes the
/// surface off screen, and the next run continues below the messages already
/// shown.
/// </summary>
public sealed partial class SubtitleOverlayWindow : Window
{
    /// <summary>Distance from the bottom that still counts as reading the newest message.</summary>
    private const double FollowThreshold = 8d;

    private const double TranslatedFontSize = 22d;
    private const double OriginalFontSize = 15d;

    private static readonly SolidColorBrush TranslatedTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush OriginalTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 175, 195, 214));
    private static readonly SolidColorBrush SpeakerTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 143, 232, 222));
    private static readonly SolidColorBrush MessageBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255));

    private readonly SubtitleCaptionBuffer _captions;
    private readonly List<CaptionBubble> _bubbles = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly double _initialOpacity;
    private OverlayWindowController? _windowController;
    private bool _followLatest = true;

#pragma warning disable CS0067 // Kept for binary/source compatibility with the pre-V2 overlay host.
    public event EventHandler<SubtitleLanguageChangedEventArgs>? LanguageChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Raised when the user closes the caption window. Closing it is the same
    /// decision as turning other-player captions off, so the host routes the
    /// request through the serialized master switch instead of hiding the surface
    /// on its own.
    /// </summary>
    internal event EventHandler? SurfaceCloseRequested;

    public SubtitleOverlayWindow(double initialOpacity = 0.90)
    {
        InitializeComponent();
        // The presentation option is read once per window; the voice page pushes
        // later changes through ApplyContentMode.
        _captions = new SubtitleCaptionBuffer(SubtitleCaptionBuffer.DefaultCapacity, SubtitleCaptionSettings.Read());
        _initialOpacity = Math.Clamp(initialOpacity, 0.60, 1.00);
        ExtendsContentIntoTitleBar = false;
        Title = "字幕";

        CaptionScroll.ViewChanged += OnCaptionViewChanged;
        NewCaptionHint.Click += OnNewCaptionHintClicked;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        EnsureWindowController();
        Closed += OnClosed;
    }

    private void EnsureWindowController()
    {
        if (_windowController is not null) return;
        try
        {
            _windowController = new OverlayWindowController(
                this,
                "字幕",
                1400,
                180,
                OverlayWindowHost.GetSavedLayout(OverlayWindowHost.SubtitleLayoutKey),
                layout => OverlayWindowHost.SaveLayout(OverlayWindowHost.SubtitleLayoutKey, layout),
                _initialOpacity,
                OnSurfaceCloseRequested);
        }
        catch
        {
            // The caption surface remains usable if a platform window API is
            // unavailable (for example, under a UI test host).
        }
    }

    /// <summary>
    /// The owner of this surface decides what closing it means; without one the
    /// window still hides itself so it can never become unclosable.
    /// </summary>
    private void OnSurfaceCloseRequested()
    {
        var handler = SurfaceCloseRequested;
        if (handler is null)
        {
            HideOverlay();
            return;
        }

        handler(this, EventArgs.Empty);
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
        // A hidden surface does not lay out its messages, so catch up once it is
        // visible again - but only for a reader who was already at the newest one.
        if (_followLatest) _dispatcher?.TryEnqueue(ScrollToLatest);
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

    // The local recognizer automatically handles the supported voice set
    // (English, Japanese, and Korean). There is no language picker in the
    // game overlay; keep this property for callers that persisted old state.
    public string SelectedSourceLanguage => "auto";

    // Kept for the existing VoicePage contract. Subtitle output is always
    // Simplified Chinese, so there is no target-language control in the UI.
    public string SelectedTargetLanguage => "zh-CN";

    public void SetLanguageSelection(string sourceLanguage, string targetLanguage)
    {
        // Compatibility no-op. Recognition language is selected by the local
        // model and subtitle output is always Simplified Chinese.
        LanguageChanged?.Invoke(this, new SubtitleLanguageChangedEventArgs("auto", "zh-CN"));
    }

    /// <summary>
    /// Adds one recognized sentence as a new message and keeps the newest one in
    /// view while the reader is at the bottom. A paused stream drops the caption
    /// instead of replacing what is on the surface.
    /// </summary>
    public void AppendCaption(string original, string translated, string? speakerLabel = null)
    {
        var caption = new SubtitleCaption(original, translated, speakerLabel);
        var follow = _followLatest;
        var droppedBefore = _captions.DroppedCount;
        if (!_captions.Append(caption)) return;

        TrimSurface(_captions.DroppedCount - droppedBefore);
        var bubble = CreateBubble(caption);
        _bubbles.Add(bubble);
        CaptionMessages.Children.Add(bubble.Container);
        AnimateMessage(bubble.Container);
        if (follow)
        {
            NewCaptionHint.Visibility = Visibility.Collapsed;
            ScrollToLatest();
        }
        else
        {
            // The reader is reviewing older messages; never pull them to the
            // bottom, only offer the way back.
            NewCaptionHint.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Pauses the message stream while recognition is stopped. Existing messages
    /// stay exactly as they are and the next run continues below them.
    /// </summary>
    internal void SetStreamPaused(bool paused)
    {
        if (paused) _captions.Pause();
        else _captions.Resume();
    }

    /// <summary>Switches between "仅译文" and "译文 + 原文" for existing and new messages.</summary>
    internal void ApplyContentMode(SubtitleContentMode mode)
    {
        if (_captions.ContentMode == mode) return;
        _captions.SetContentMode(mode);
        if (_bubbles.Count == 0) return;

        var follow = _followLatest;
        var offset = CaptionScroll.VerticalOffset;
        foreach (var bubble in _bubbles) RenderBody(bubble);
        if (follow)
        {
            ScrollToLatest();
            return;
        }

        try
        {
            CaptionScroll.UpdateLayout();
            CaptionScroll.ChangeView(null, offset, null, disableAnimation: true);
        }
        catch
        {
            // A presentation change must never break the surface.
        }
    }

    private CaptionBubble CreateBubble(SubtitleCaption caption)
    {
        var body = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var container = new Border
        {
            Child = body,
            Background = MessageBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var bubble = new CaptionBubble(caption, container, body);
        RenderBody(bubble);
        return bubble;
    }

    /// <summary>
    /// Renders the lines of one message from the shared projection, so the
    /// "仅译文 / 译文 + 原文" choice stays one rule for the model and the surface.
    /// Every line - speaker label, translation and original - is centred across
    /// the full width of the message strip.
    /// </summary>
    private void RenderBody(CaptionBubble bubble)
    {
        bubble.Body.Children.Clear();
        if (bubble.Caption.HasSpeaker)
        {
            bubble.Body.Children.Add(new TextBlock
            {
                Text = bubble.Caption.SpeakerLabel!,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = SpeakerTextBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            });
        }

        foreach (var line in SubtitleCaptionBuffer.Present(bubble.Caption, _captions.ContentMode))
        {
            bubble.Body.Children.Add(new TextBlock
            {
                Text = line.Text,
                FontSize = line.IsOriginal ? OriginalFontSize : TranslatedFontSize,
                FontWeight = line.IsOriginal ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = line.IsOriginal ? OriginalTextBrush : TranslatedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }
    }

    /// <summary>Drops the messages the bounded log already discarded.</summary>
    private void TrimSurface(int dropped)
    {
        for (var index = 0; index < dropped && _bubbles.Count > 0; index++)
        {
            var oldest = _bubbles[0];
            _bubbles.RemoveAt(0);
            CaptionMessages.Children.Remove(oldest.Container);
        }
    }

    private void ScrollToLatest()
    {
        try
        {
            // ScrollableHeight only reflects the new message after a layout pass.
            CaptionScroll.UpdateLayout();
            CaptionScroll.ChangeView(null, CaptionScroll.ScrollableHeight, null, disableAnimation: true);
            _followLatest = true;
        }
        catch
        {
            // A surface that cannot scroll yet still shows the newest message.
        }
    }

    private void OnCaptionViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _followLatest = CaptionScroll.ScrollableHeight - CaptionScroll.VerticalOffset <= FollowThreshold;
        if (_followLatest) NewCaptionHint.Visibility = Visibility.Collapsed;
    }

    private void OnNewCaptionHintClicked(object sender, RoutedEventArgs e)
    {
        NewCaptionHint.Visibility = Visibility.Collapsed;
        ScrollToLatest();
    }

    private void AnimateMessage(UIElement target)
    {
        try
        {
            target.Opacity = 0.45;
            var animation = new DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(120))
            };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            target.Opacity = 1;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CaptionScroll.ViewChanged -= OnCaptionViewChanged;
        NewCaptionHint.Click -= OnNewCaptionHintClicked;
        Closed -= OnClosed;
    }

    /// <summary>One rendered message: its model, its container and its line host.</summary>
    private sealed record CaptionBubble(SubtitleCaption Caption, Border Container, StackPanel Body);
}

public sealed record SubtitleLanguageChangedEventArgs(string SourceLanguage, string TargetLanguage);
