using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using VrcTranslate.Application.Subtitles;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Compact caption surface for other players' voices. The output language is
/// fixed to Simplified Chinese and the surface has no visible configuration and
/// no activity indicator: activity shows up as the messages that arrive, while
/// the level meter on the voice page owns input feedback. Recognized sentences
/// accumulate as player-style messages that exist for this run only - nothing is
/// written to disk and there is no clear action. Stopping recognition takes the
/// surface off screen, and the next run continues below the messages already
/// shown.
/// </summary>
public sealed partial class SubtitleOverlayWindow : Window
{
    /// <summary>Distance from the bottom that still counts as reading the newest message.</summary>
    private const double FollowThreshold = 8d;

    /// <summary>Translation size.</summary>
    private const double TranslatedFontSize = 18d;

    /// <summary>One step down for a narrow surface, so a sentence keeps its shape.</summary>
    private const double NarrowTranslatedFontSize = 16d;

    /// <summary>Surface width below which the translation steps down.</summary>
    private const double NarrowSurfaceWidth = 420d;

    /// <summary>Recognized line: auxiliary information, so it stays smaller.</summary>
    private const double OriginalFontSize = 13d;

    private const double SpeakerFontSize = 12d;

    /// <summary>Line height shared by every caption line: about 1.35 of its font size.</summary>
    private const double LineHeightRatio = 1.35d;

    /// <summary>Vertical gap between two messages.</summary>
    private const double MessageSpacing = 9d;

    /// <summary>Left and right padding shared by the speaker label and both text lines.</summary>
    private const double MessagePadding = 12d;

    /// <summary>Air above the first line of a message.</summary>
    private const double MessagePaddingTop = 8d;

    /// <summary>Hairline that separates two messages instead of a bubble around each.</summary>
    private const double SeparatorThickness = 1d;

    /// <summary>Shown after a recognized line whose translation failed or came back empty.</summary>
    private const string MissingTranslationSuffix = "（未翻译）";

    /// <summary>Default surface height; also the ceiling for a first run without saved layout.</summary>
    private const int DefaultSurfaceHeight = 180;

    /// <summary>Height difference that still counts as the height this surface asked for.</summary>
    private const int LayoutTolerance = 2;

    /// <summary>Smallest height a reader's own resize may persist.</summary>
    private const int MinimumStoredHeight = 56;

    private static readonly SolidColorBrush TranslatedTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255));
    // One step darker than before: next to the 18px translation the recognized
    // line has to read as secondary information.
    private static readonly SolidColorBrush OriginalTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 147, 167, 188));
    private static readonly SolidColorBrush SpeakerTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 143, 232, 222));
    // A faint rule between messages on the one dark surface. It stays below the
    // green/teal accent range the surface must never carry.
    private static readonly SolidColorBrush SeparatorBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 38, 54, 75));

    private readonly SubtitleCaptionBuffer _captions;
    private readonly List<CaptionBubble> _bubbles = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly double _initialOpacity;
    private OverlayWindowController? _windowController;
    private bool _followLatest = true;
    private bool _narrowSurface;
    private bool _fittingHeight;

    /// <summary>
    /// True once the reader sized the surface by hand. Their rectangle is then
    /// final for this session: following the content must never shrink a height
    /// the reader just chose.
    /// </summary>
    private bool _readerOwnsHeight;

    /// <summary>Height the reader chose; the ceiling for the content-following height.</summary>
    private int _preferredHeight;

    /// <summary>Height this surface last asked the native window for; 0 before the first fit.</summary>
    private int _appliedHeight;

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
        OverlaySurface.SizeChanged += OnSurfaceSizeChanged;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        EnsureWindowController();
        Closed += OnClosed;
    }

    private void EnsureWindowController()
    {
        if (_windowController is not null) return;
        try
        {
            var saved = OverlayWindowHost.GetSavedLayout(OverlayWindowHost.SubtitleLayoutKey);
            // A saved height is the reader's own ceiling for the surface; a first
            // run uses the same default the native controller falls back to.
            _preferredHeight = saved is { IsUsable: true } usable ? usable.Height : DefaultSurfaceHeight;
            _windowController = new OverlayWindowController(
                this,
                "字幕",
                1400,
                180,
                saved,
                layout => OverlayWindowHost.SaveLayout(OverlayWindowHost.SubtitleLayoutKey, ToStoredLayout(layout)),
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
        if (_followLatest)
        {
            _dispatcher?.TryEnqueue(() =>
            {
                FitSurfaceHeight();
                ScrollToLatest();
            });
        }
    }

    internal void HideOverlay()
    {
        if (_windowController is not null) _windowController.Hide();
        else OverlayWindowController.HideFallback(this);
    }

    internal void ApplyOpacity(double opacity) => _windowController?.ApplyOpacity(opacity);

    internal bool TryGetLayout(out OverlayWindowLayout layout)
    {
        if (_windowController is not null && _windowController.TryGetLayout(out layout))
        {
            layout = ToStoredLayout(layout);
            return true;
        }

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
    /// Shows one recognized sentence the moment recognition produced it: the
    /// message carries its speaker label and the recognized line while the
    /// translation is still on its way. <paramref name="captionId"/> is the
    /// caller's correlation id, and <see cref="FillCaptionTranslation"/> completes
    /// this very message with it, so recognition and translation can never turn
    /// one sentence into two messages. A paused stream drops the caption instead
    /// of replacing what is on the surface.
    /// </summary>
    public bool AppendRecognizedCaption(long captionId, string original, string? speakerLabel = null)
    {
        if (captionId <= 0) return false;
        return AddCaption(SubtitleCaption.Pending(original, speakerLabel, captionId));
    }

    /// <summary>
    /// Adds one complete message in a single call. The progressive path uses
    /// <see cref="AppendRecognizedCaption"/> and <see cref="FillCaptionTranslation"/>.
    /// </summary>
    public void AppendCaption(string original, string translated, string? speakerLabel = null) =>
        _ = AddCaption(new SubtitleCaption(original, translated, speakerLabel));

    /// <summary>
    /// Fills the translation into the message that was created for this id. The
    /// message keeps its position and the list keeps its length: only the lines of
    /// that one message change. A null or empty translation resolves the message
    /// as "no translation", so the recognized line stays visible on its own
    /// instead of a message that waits forever.
    /// </summary>
    public bool FillCaptionTranslation(long captionId, string? translated)
    {
        var index = _bubbles.FindIndex(bubble => bubble.Caption.Id == captionId);
        if (index < 0) return false;
        if (!_captions.TrySetTranslation(captionId, translated)) return false;
        if (!_captions.TryGetCaption(captionId, out var resolved)) return false;

        var bubble = _bubbles[index] with { Caption = resolved };
        _bubbles[index] = bubble;
        RenderBody(bubble);
        FitSurfaceHeight();
        // The reader is looking at the newest message, so the extra line must not
        // push it out of view. A reader who scrolled back is never pulled down.
        if (_followLatest) ScrollToLatest();
        return true;
    }

    private bool AddCaption(SubtitleCaption caption)
    {
        var follow = _followLatest;
        var droppedBefore = _captions.DroppedCount;
        if (!_captions.Append(caption)) return false;

        TrimSurface(_captions.DroppedCount - droppedBefore);
        var bubble = CreateBubble(caption);
        _bubbles.Add(bubble);
        CaptionMessages.Children.Add(bubble.Container);
        RefreshSeparators();
        AnimateMessage(bubble.Container);
        FitSurfaceHeight();
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

        return true;
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
        FitSurfaceHeight();
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
        // No rounded bubble around a message: one dark surface with a hairline
        // between messages reads like the player's own subtitles and keeps the
        // captions quieter.
        var container = new Border
        {
            Child = body,
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, SeparatorThickness, 0, 0),
            Padding = new Thickness(MessagePadding, MessagePaddingTop, MessagePadding, 0),
            Margin = new Thickness(0, 0, 0, MessageSpacing),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var bubble = new CaptionBubble(caption, container, body);
        RenderBody(bubble);
        return bubble;
    }

    /// <summary>
    /// Renders the lines of one message from the shared projection, so the
    /// "仅译文 / 译文 + 原文" choice stays one rule for the model and the surface.
    /// Every line - speaker label, translation and recognized line - shares one
    /// alignment, one left/right padding and one line height; only font size,
    /// weight and colour separate the primary content from the auxiliary line, and
    /// the translation is always the line above the recognized one.
    /// </summary>
    private void RenderBody(CaptionBubble bubble)
    {
        bubble.Body.Children.Clear();
        if (bubble.Caption.HasSpeaker)
        {
            bubble.Body.Children.Add(new TextBlock
            {
                Text = bubble.Caption.SpeakerLabel!,
                FontSize = SpeakerFontSize,
                LineHeight = LineHeightFor(SpeakerFontSize),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
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
            bubble.Body.Children.Add(CreateLineBlock(line));
        }
    }

    /// <summary>
    /// One rendered line. A line that stands in for a translation which never
    /// arrived says so, so a failed or empty translation is recognizable instead
    /// of looking like a finished caption.
    /// </summary>
    private TextBlock CreateLineBlock(SubtitleCaptionLine line)
    {
        var fontSize = line.IsOriginal
            ? OriginalFontSize
            : _narrowSurface ? NarrowTranslatedFontSize : TranslatedFontSize;
        var translationMissing = line.TranslationState == SubtitleTranslationState.Unavailable;
        return new TextBlock
        {
            Text = translationMissing ? line.Text + MissingTranslationSuffix : line.Text,
            FontSize = fontSize,
            LineHeight = LineHeightFor(fontSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontWeight = line.IsOriginal ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = line.IsOriginal ? OriginalTextBrush : TranslatedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    /// <summary>Line height shared by every caption line, so both sizes breathe alike.</summary>
    private static double LineHeightFor(double fontSize) => Math.Round(fontSize * LineHeightRatio, 2);

    /// <summary>
    /// Draws the hairline only between messages: the oldest visible message starts
    /// the block instead of being underlined by a rule of its own.
    /// </summary>
    private void RefreshSeparators()
    {
        for (var index = 0; index < _bubbles.Count; index++)
        {
            _bubbles[index].Container.BorderThickness = index == 0
                ? new Thickness(0)
                : new Thickness(0, SeparatorThickness, 0, 0);
        }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowSurfaceWidth;
        if (narrow != _narrowSurface)
        {
            _narrowSurface = narrow;
            foreach (var bubble in _bubbles) RenderBody(bubble);
            FitSurfaceHeight();
            return;
        }

        // The surface just received a real size for the first time (the window
        // became visible), so the messages that are already there can be followed.
        // A resize by the reader is deliberately not answered with a fit: the
        // rectangle they chose stands for the rest of the session.
        if (e.PreviousSize.Height <= 0d && e.NewSize.Height > 0d) FitSurfaceHeight();
    }

    /// <summary>
    /// Keeps the native window as tall as the messages need and no taller, so a
    /// single caption never sits under a large empty surface. The top edge stays
    /// where the reader put it and the strip grows downwards - the same direction
    /// the top-aligned messages grow - so the captions already on screen keep
    /// their place while a new one arrives below them. The reader's own height
    /// stays the ceiling, which is also the height that is persisted.
    /// </summary>
    private void FitSurfaceHeight()
    {
        if (_fittingHeight || _readerOwnsHeight) return;
        // Nothing to follow yet: a surface without messages keeps the rectangle
        // the reader left it with instead of collapsing to a strip.
        if (_bubbles.Count == 0) return;
        _fittingHeight = true;
        try
        {
            var controller = _windowController;
            if (controller is null || _preferredHeight <= 0) return;
            var scale = OverlaySurface.XamlRoot?.RasterizationScale ?? 0d;
            if (scale <= 0d) return;
            if (!controller.TryGetLayout(out var current) || !current.IsUsable) return;

            var content = MeasureContentHeight();
            if (content < 0d) return;
            var client = Math.Max(content, OverlaySurface.MinHeight);
            var chrome = current.Height - (int)Math.Round(OverlaySurface.ActualHeight * scale);
            var target = (int)Math.Min(Math.Ceiling(client * scale) + Math.Max(chrome, 0), _preferredHeight);
            var previous = _appliedHeight;
            // Recorded before the native call: the layout callback that a resize
            // raises has to recognize this height as the surface's own instead of
            // mistaking it for a resize by the reader.
            _appliedHeight = target;
            if (!controller.ResizeKeepingTop(target)) _appliedHeight = previous;
        }
        catch
        {
            // Following the content is presentation polish; it must never break
            // the caption surface.
        }
        finally
        {
            _fittingHeight = false;
        }
    }

    /// <summary>
    /// Height of the message list in surface units, or a negative value when it
    /// has not been laid out yet - the current height is then left alone. The
    /// stack lives in a vertical ScrollViewer, so it is measured with an
    /// unbounded height and its actual height is the whole content.
    /// </summary>
    private double MeasureContentHeight()
    {
        CaptionScroll.UpdateLayout();
        var messages = CaptionMessages.ActualHeight;
        if (messages <= 0d && _bubbles.Count > 0) return -1d;

        return messages
            + CaptionScroll.Padding.Top + CaptionScroll.Padding.Bottom
            + CaptionScroll.BorderThickness.Top + CaptionScroll.BorderThickness.Bottom
            + CaptionLayout.Margin.Top + CaptionLayout.Margin.Bottom;
    }

    /// <summary>
    /// The content-following height is a runtime presentation detail: persisting
    /// it would cap every later run at whatever the messages happened to measure
    /// when the window closed. The reader's own height is stored instead, anchored
    /// at the top edge where the surface currently is.
    /// </summary>
    private OverlayWindowLayout ToStoredLayout(OverlayWindowLayout live)
    {
        if (_appliedHeight > 0 && Math.Abs(live.Height - _appliedHeight) <= LayoutTolerance)
        {
            // The content-following height is a runtime detail: persist the height
            // the reader chose. Following the content keeps the top edge, so the
            // current position is already the reader's own anchor.
            return live with { Height = _preferredHeight };
        }

        // Any other height is the reader's. It becomes the ceiling, and it is final
        // for this session: moving the surface keeps the same height, so only a real
        // height change stops the surface from following the content.
        if (Math.Abs(live.Height - _preferredHeight) > LayoutTolerance) _readerOwnsHeight = true;
        _preferredHeight = Math.Max(live.Height, MinimumStoredHeight);
        return live;
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
        OverlaySurface.SizeChanged -= OnSurfaceSizeChanged;
        Closed -= OnClosed;
    }

    /// <summary>One rendered message: its model, its container and its line host.</summary>
    private sealed record CaptionBubble(SubtitleCaption Caption, Border Container, StackPanel Body);
}

public sealed record SubtitleLanguageChangedEventArgs(string SourceLanguage, string TargetLanguage);
