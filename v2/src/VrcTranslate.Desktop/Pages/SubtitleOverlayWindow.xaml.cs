using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Compact subtitle surface for other players' voices. The output language is
/// fixed to Simplified Chinese and the surface has no visible configuration.
/// </summary>
public sealed partial class SubtitleOverlayWindow : Window
{
    private string _lastOriginal = string.Empty;
    private string _lastTranslated = string.Empty;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualTimer;
    private readonly ScaleTransform _pulseTransform;
    private double _visualPhase;
    private bool _hasCaption;

#pragma warning disable CS0067 // Kept for binary/source compatibility with the pre-V2 overlay host.
    public event EventHandler<SubtitleLanguageChangedEventArgs>? LanguageChanged;
#pragma warning restore CS0067

    public SubtitleOverlayWindow()
    {
        InitializeComponent();
        // Set this before the first activation so the native caption area is
        // part of the client surface from the first frame.
        ExtendsContentIntoTitleBar = true;
        Title = string.Empty;
        SubtitleText.Text = string.Empty;
        PulseRing.Opacity = 0.34;
        _pulseTransform = PulseRing.RenderTransform as ScaleTransform ?? new ScaleTransform();
        if (PulseRing.RenderTransform is not ScaleTransform)
        {
            PulseRing.RenderTransform = _pulseTransform;
        }
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is not null)
        {
            _visualTimer = dispatcher.CreateTimer();
            _visualTimer.Interval = TimeSpan.FromMilliseconds(90);
            _visualTimer.Tick += OnVisualTimerTick;
            _visualTimer.Start();
        }

        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        try
        {
            OverlayWindowChrome.Configure(
                this,
                DragSurface,
                1400,
                150,
                alwaysOnTop: true,
                fullCaption: true,
                initialLayout: OverlayWindowHost.GetSavedLayout(OverlayWindowHost.SubtitleLayoutKey),
                layoutChanged: layout => OverlayWindowHost.SaveLayout(OverlayWindowHost.SubtitleLayoutKey, layout));
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch
        {
            // The caption surface remains usable if a platform window API is
            // unavailable (for example, under a UI test host).
        }
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

    public void SetSubtitle(string original, string translated)
    {
        _lastOriginal = original?.Trim() ?? string.Empty;
        _lastTranslated = translated?.Trim() ?? string.Empty;
        RefreshSubtitle();
    }

    private void RefreshSubtitle()
    {
        // Chinese users only need the translated line. Keep the surface quiet
        // while a recognizer is waiting for the translation response.
        var text = _lastTranslated;
        var hasCaption = !string.IsNullOrWhiteSpace(text);
        _hasCaption = hasCaption;
        SubtitleText.Text = text;
        PulseRing.Opacity = hasCaption ? 0.58 : 0.34;
        if (!hasCaption)
        {
            SubtitleText.Opacity = 1;
            return;
        }

        try
        {
            SubtitleText.Opacity = 0.45;
            var animation = new DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(120))
            };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, SubtitleText);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            SubtitleText.Opacity = 1;
        }
    }

    private void OnVisualTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _visualPhase += _hasCaption ? 0.34 : 0.18;
        // The ring breathes instead of flashing, which remains readable over a
        // moving game scene without adding a second status widget.
        var ringOpacity = (_hasCaption ? 0.42 : 0.25) +
                          (_hasCaption ? 0.18 : 0.08) * (0.5 + 0.5 * Math.Sin(_visualPhase * 0.7));
        PulseRing.Opacity = ringOpacity;
        var pulseAmplitude = _hasCaption ? 0.08 : 0.05;
        var pulseScale = 1.0 + pulseAmplitude * (0.5 + 0.5 * Math.Sin(_visualPhase));
        _pulseTransform.ScaleX = pulseScale;
        _pulseTransform.ScaleY = pulseScale;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_visualTimer is not null)
        {
            _visualTimer.Stop();
            _visualTimer.Tick -= OnVisualTimerTick;
        }

        Closed -= OnClosed;
    }

}

public sealed record SubtitleLanguageChangedEventArgs(string SourceLanguage, string TargetLanguage);
