using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using VrcTranslate.Application.Subtitles;
using VrcTranslate.Core.Settings;
using VrcTranslate.Core.Speech;

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

    /// <summary>Stable per-speaker colours; only the left rail and the label use them.</summary>
    private static readonly SolidColorBrush[] SpeakerBrushes =
    [
        new(Microsoft.UI.ColorHelper.FromArgb(255, 143, 232, 222)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 242, 200, 121)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 199, 166, 255)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 127, 200, 255)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 255, 158, 181)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 182, 227, 155)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 255, 179, 122)),
        new(Microsoft.UI.ColorHelper.FromArgb(255, 214, 220, 229)),
    ];
    // A faint rule between messages on the one dark surface. It stays below the
    // green/teal accent range the surface must never carry.
    private static readonly SolidColorBrush SeparatorBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 38, 54, 75));

    /// <summary>Neutral but legible on the dark surface; used for "未知".</summary>
    private static readonly SolidColorBrush UnknownSpeakerBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 154, 167, 181));

    private readonly SubtitleCaptionBuffer _captions;
    private readonly List<CaptionBubble> _bubbles = [];
    private readonly Dictionary<string, SolidColorBrush> _speakerBrushes = new(StringComparer.Ordinal);
    private int _nextSpeakerBrush;
    private double _lastScrollableHeight;
    private double _lastVerticalOffset;
    private bool _programmaticScroll;
    private DateTime _programmaticScrollUntil;
    private int _scrollAttempts;
    private int _scrollGeneration;
    private bool _followCorrectionQueued;
    private DateTime _lastUserScrollAt;
    private bool _pointerPressed;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly double _initialOpacity;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _contentCheckTimer;
    private OverlayWindowController? _windowController;
    private bool _followLatest = true;
    private bool _narrowSurface;
    private bool _fittingHeight;
    private bool _contentRecoveryAttempted;
    private string? _persistentStatus;

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
        CaptionScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        CaptionScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        CaptionScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        CaptionScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        CaptionScroll.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerReleased), true);
        CaptionScroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnScrollKeyDown), true);
        CaptionScroll.AddHandler(UIElement.ManipulationStartedEvent, new ManipulationStartedEventHandler((_, _) => MarkUserScroll()), true);
        CaptionScroll.AddHandler(UIElement.ManipulationDeltaEvent, new ManipulationDeltaEventHandler((_, _) => MarkUserScroll()), true);
        NewCaptionHint.Click += OnNewCaptionHintClicked;
        OverlaySurface.SizeChanged += OnSurfaceSizeChanged;
        OverlaySurface.Loaded += OnOverlayLoaded;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _contentCheckTimer = _dispatcher?.CreateTimer();
        if (_contentCheckTimer is not null)
        {
            _contentCheckTimer.Interval = TimeSpan.FromMilliseconds(400);
            _contentCheckTimer.IsRepeating = false;
            _contentCheckTimer.Tick += OnContentCheckTick;
        }

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
        // The surface is only shown while recognition runs, so a visible window
        // is the strongest signal that the stream must accept captions. Tying the
        // pause state to visibility removes the dependency on event ordering from
        // the running-changed notification.
        _captions.Resume();
        UpdateTitle();
        if (_windowController is not null) _windowController.Show(activate);
        else OverlayWindowController.ShowFallback(this, activate);
        CaptionDiagnostics.Write($"overlay show paused={_captions.IsPaused} messages={_bubbles.Count}");
        _contentCheckTimer?.Start();

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
        _captions.Pause();
        _contentCheckTimer?.Stop();
        CaptionDiagnostics.Write("overlay hide");
        if (_windowController is not null) _windowController.Hide();
        else OverlayWindowController.HideFallback(this);
    }

    /// <summary>
    /// Puts the startup self-test verdict on the window's title, where it stays
    /// visible even if the XAML content itself did not render. A passing check
    /// keeps the plain "字幕"; the first real caption also clears it, so a false
    /// negative on the self-test cannot leave a warning on a working surface.
    /// </summary>
    internal void SetSelfTestResult(LocalSpeechSelfTestResult? result)
    {
        _persistentStatus = result is { IsUsable: false } ? "自检失败" : null;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var title = _persistentStatus is { Length: > 0 } ? $"字幕（{_persistentStatus}）" : "字幕";
        if (!string.Equals(Title, title, StringComparison.Ordinal)) Title = title;
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
        if (index < 0)
        {
            CaptionDiagnostics.Write($"caption fill ignored id={captionId} reason=missing");
            return false;
        }

        if (!_captions.TrySetTranslation(captionId, translated))
        {
            CaptionDiagnostics.Write($"caption fill ignored id={captionId} reason=not-pending");
            return false;
        }

        if (!_captions.TryGetCaption(captionId, out var resolved)) return false;

        var bubble = _bubbles[index] with { Caption = resolved };
        _bubbles[index] = bubble;
        RenderBody(bubble);
        FitSurfaceHeight();
        CaptionDiagnostics.Write(
            $"caption filled id={captionId} hasTranslation={!string.IsNullOrWhiteSpace(translated)}");
        // The reader is looking at the newest message, so the extra line must not
        // push it out of view. A reader who scrolled back is never pulled down.
        if (_followLatest) ScrollToLatest();
        return true;
    }

    private bool AddCaption(SubtitleCaption caption)
    {
        var follow = _followLatest;
        var droppedBefore = _captions.DroppedCount;
        if (!_captions.Append(caption))
        {
            CaptionDiagnostics.Write(
                $"caption dropped id={caption.Id} paused={_captions.IsPaused} empty={caption.IsEmpty}");
            return false;
        }

        // A real caption proves the path works; a persistent self-test warning
        // must not outlive it.
        _persistentStatus = null;
        UpdateTitle();
        CaptionDiagnostics.Write($"caption appended id={caption.Id} chars={caption.Original.Length}");

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
        var speakerBrush = SpeakerBrushFor(caption.SpeakerLabel);
        var body = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(MessagePadding, MessagePaddingTop, MessagePadding, 0),
        };
        // A 3px colour rail is the only speaker-specific chrome; the message
        // itself stays on the same dark surface. Consecutive messages by the
        // same speaker share the rail and drop the separator (see RefreshSeparators).
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rail = new Border
        {
            Width = 3,
            Background = speakerBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(body, 1);
        content.Children.Add(rail);
        content.Children.Add(body);

        var container = new Border
        {
            Child = content,
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, SeparatorThickness, 0, 0),
            Margin = new Thickness(0, 0, 0, MessageSpacing),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var bubble = new CaptionBubble(caption, container, body, speakerBrush, true);
        RenderBody(bubble);
        if (caption.HasSpeaker)
        {
            var reject = new MenuFlyoutItem { Text = "不是此人" };
            reject.Click += (_, _) => RejectSpeaker(bubble);
            var menu = new MenuFlyout();
            menu.Items.Add(reject);
            container.ContextFlyout = menu;
        }

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
        // Every message carries its label. A same-speaker run still shares the
        // rail/colour and drops the separator, but no line is left ambiguous.
        if (bubble.Caption.HasSpeaker)
        {
            bubble.Body.Children.Add(new TextBlock
            {
                Text = bubble.Caption.SpeakerLabel!,
                FontSize = SpeakerFontSize,
                LineHeight = LineHeightFor(SpeakerFontSize),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                FontWeight = FontWeights.SemiBold,
                Foreground = bubble.SpeakerBrush,
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
            var sameSpeaker = index > 0 && SameSpeaker(_bubbles[index - 1].Caption, _bubbles[index].Caption);
            _bubbles[index].Container.BorderThickness = index == 0 || sameSpeaker
                ? new Thickness(0)
                : new Thickness(0, SeparatorThickness, 0, 0);

            var showLabel = index == 0 || !sameSpeaker;
            if (_bubbles[index].ShowSpeakerLabel != showLabel)
            {
                _bubbles[index].ShowSpeakerLabel = showLabel;
                RenderBody(_bubbles[index]);
            }
        }
    }

    private static bool SameSpeaker(SubtitleCaption previous, SubtitleCaption current) =>
        previous.HasSpeaker &&
        current.HasSpeaker &&
        string.Equals(previous.SpeakerLabel, current.SpeakerLabel, StringComparison.Ordinal);

    private SolidColorBrush SpeakerBrushFor(string? speakerLabel)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel)) return SeparatorBrush;
        if (speakerLabel == "未知") return UnknownSpeakerBrush;
        if (_speakerBrushes.TryGetValue(speakerLabel, out var existing)) return existing;
        var brush = SpeakerBrushes[_nextSpeakerBrush++ % SpeakerBrushes.Length];
        _speakerBrushes[speakerLabel] = brush;
        return brush;
    }

    private void RejectSpeaker(CaptionBubble bubble)
    {
        var index = _bubbles.FindIndex(item => ReferenceEquals(item, bubble));
        if (index < 0) return;

        var label = bubble.Caption.SpeakerLabel;
        if (!string.IsNullOrWhiteSpace(label) &&
            Microsoft.UI.Xaml.Application.Current is App app &&
            app.State.LocalSpeech.Speakers is { } speakers)
        {
            var identity = speakers.Speakers.FirstOrDefault(item =>
                string.Equals(item.DisplayName, label, StringComparison.Ordinal));
            if (identity is not null)
            {
                try { speakers.Reject(identity.Id); }
                catch { /* a correction must never break the caption surface */ }
            }
        }

        var updated = bubble with
        {
            Caption = bubble.Caption.WithSpeaker("未知"),
            SpeakerBrush = UnknownSpeakerBrush,
            ShowSpeakerLabel = true,
        };
        _bubbles[index] = updated;
        RenderBody(updated);
        RefreshSeparators();
        CaptionDiagnostics.Write($"caption speaker rejected id={bubble.Caption.Id}");
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowSurfaceWidth;
        if (narrow != _narrowSurface)
        {
            _narrowSurface = narrow;
            foreach (var bubble in _bubbles) RenderBody(bubble);
            FitSurfaceHeight();
            ScheduleFollowCorrection();
            return;
        }

        // The surface just received a real size for the first time (the window
        // became visible), so the messages that are already there can be followed.
        // A resize by the reader is deliberately not answered with a fit: the
        // rectangle they chose stands for the rest of the session.
        if (e.PreviousSize.Height <= 0d && e.NewSize.Height > 0d)
        {
            FitSurfaceHeight();
        }

        // A native height change also changes the scrollable range; correct
        // after the layout settles even when the height was content-driven.
        ScheduleFollowCorrection();
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
            // One shared conversion with the input overlay: the native rectangle
            // is physical pixels while every length on this surface is DIP.
            var scale = OverlayDisplayScale.Resolve(OverlaySurface);
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
        _followLatest = true;
        _scrollAttempts = 0;
        _programmaticScroll = true;
        _programmaticScrollUntil = DateTime.UtcNow.AddMilliseconds(600);
        var generation = ++_scrollGeneration;
        TryScrollToLatest(generation);
    }

    /// <summary>
    /// Corrections are retried over several dispatcher turns: a long message or
    /// a native window height change can settle after the first layout pass, and
    /// a single ChangeView would then leave the newest line below the viewport.
    /// </summary>
    private void TryScrollToLatest(int generation)
    {
        if (!_programmaticScroll || generation != _scrollGeneration) return;
        try
        {
            CaptionScroll.UpdateLayout();
            var target = CaptionScroll.ScrollableHeight;
            CaptionScroll.ChangeView(null, target, null, disableAnimation: true);
            _lastScrollableHeight = target;
            _lastVerticalOffset = target;
            _followLatest = true;
            NewCaptionHint.Visibility = Visibility.Collapsed;
            if (++_scrollAttempts < 5)
            {
                _dispatcher?.TryEnqueue(() => TryScrollToLatest(generation));
            }
            else
            {
                _programmaticScroll = false;
            }
        }
        catch
        {
            _programmaticScroll = false;
            // A surface that cannot scroll yet still shows the newest message.
        }
    }

    /// <summary>
    /// Only a change in vertical position while the scrollable height is stable is
    /// a reader scroll. A growing message, a window resize or our own ChangeView
    /// must never turn following off.
    /// </summary>
    private void OnCaptionViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var scrollable = CaptionScroll.ScrollableHeight;
        var offset = CaptionScroll.VerticalOffset;
        var atBottom = scrollable - offset <= FollowThreshold;
        var contentChanged = Math.Abs(scrollable - _lastScrollableHeight) > 0.5;
        var userInput = DateTime.UtcNow - _lastUserScrollAt < TimeSpan.FromMilliseconds(400);
        _lastScrollableHeight = scrollable;
        _lastVerticalOffset = offset;

        if (!userInput && (_programmaticScroll || DateTime.UtcNow < _programmaticScrollUntil))
        {
            _followLatest = true;
            if (atBottom) NewCaptionHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (atBottom)
        {
            _followLatest = true;
            NewCaptionHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (contentChanged && !userInput)
        {
            // Layout, a long message, an animation or a native resize moved the
            // content; stay in follow mode and correct after it settles.
            if (_followLatest) ScheduleFollowCorrection();
            return;
        }

        _followLatest = false;
        NewCaptionHint.Visibility = Visibility.Visible;
    }

    private void ScheduleFollowCorrection()
    {
        if (!_followLatest || _followCorrectionQueued) return;
        _followCorrectionQueued = true;
        _dispatcher?.TryEnqueue(() =>
        {
            _followCorrectionQueued = false;
            if (!_followLatest) return;
            if (CaptionScroll.ScrollableHeight - CaptionScroll.VerticalOffset <= FollowThreshold) return;
            ScrollToLatest();
        });
    }

    private void MarkUserScroll()
    {
        _lastUserScrollAt = DateTime.UtcNow;
        // A reader gesture cancels any pending programmatic correction.
        _programmaticScroll = false;
        _scrollGeneration++;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) => MarkUserScroll();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerPressed = true;
        MarkUserScroll();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerPressed) MarkUserScroll();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerPressed = false;
        MarkUserScroll();
    }

    private void OnScrollKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End
            or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            MarkUserScroll();
        }
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

    private void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        OverlaySurface.Loaded -= OnOverlayLoaded;
        var scale = OverlayDisplayScale.Resolve(OverlaySurface);
        if (scale > 0d) CaptionDiagnostics.WriteDisplayScale(scale, "subtitle");
    }

    /// <summary>
    /// A window that has never been activated before its first show can present a
    /// blank XAML island on some Windows builds. The dark client area then looks
    /// exactly like "no captions" even though the message list is full. If the
    /// content reports no size shortly after the show, force a layout pass and
    /// activate the window once; both are recorded in the support log.
    /// </summary>
    private void OnContentCheckTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _contentCheckTimer?.Stop();
        if (_contentRecoveryAttempted || !IsOverlayVisible) return;

        var width = OverlaySurface.ActualWidth;
        var height = OverlaySurface.ActualHeight;
        if (width >= 1d && height >= 1d) return;

        _contentRecoveryAttempted = true;
        CaptionDiagnostics.Write($"overlay content blank width={width:0} height={height:0}; recovering");
        try
        {
            OverlaySurface.UpdateLayout();
            Activate();
        }
        catch (Exception exception)
        {
            CaptionDiagnostics.WriteException("overlay content recovery", exception);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CaptionScroll.ViewChanged -= OnCaptionViewChanged;
        NewCaptionHint.Click -= OnNewCaptionHintClicked;
        OverlaySurface.SizeChanged -= OnSurfaceSizeChanged;
        OverlaySurface.Loaded -= OnOverlayLoaded;
        if (_contentCheckTimer is not null) _contentCheckTimer.Tick -= OnContentCheckTick;
        _contentCheckTimer?.Stop();
        Closed -= OnClosed;
    }

    /// <summary>One rendered message: its model, its container and its line host.</summary>
    private sealed record CaptionBubble(
        SubtitleCaption Caption,
        Border Container,
        StackPanel Body,
        SolidColorBrush SpeakerBrush,
        bool ShowSpeaker)
    {
        public bool ShowSpeakerLabel { get; set; } = ShowSpeaker;
    }
}

public sealed record SubtitleLanguageChangedEventArgs(string SourceLanguage, string TargetLanguage);
