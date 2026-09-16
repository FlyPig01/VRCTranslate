using VrcTranslate.Application.Subtitles;
using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Configuration;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Owns the two game overlays for the lifetime of the desktop shell. Keeping
/// them here avoids creating a second window every time a page is revisited and
/// gives the global hotkey route one source of truth for visibility.
/// </summary>
internal static class OverlayWindowHost
{
    private static QuickInputWindow? _quickInput;
    private static SubtitleOverlayWindow? _subtitle;
    private static AppState? _state;
    private static OverlayWindowLayoutStore? _layoutStore;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private static bool _shutdownRequested;

    public const string QuickInputLayoutKey = "quick-input";
    public const string SubtitleLayoutKey = "subtitle";

    public static QuickInputWindow? QuickInput => _quickInput;

    public static SubtitleOverlayWindow? Subtitle => _subtitle;

    /// <summary>
    /// Shows one recognized sentence on the caption surface from any thread, as
    /// soon as recognition produced it and before its translation exists.
    /// Recognition results are processed by the application-scoped session host
    /// off the UI thread, so the marshal happens here instead of at every call
    /// site. <paramref name="captionId"/> is the caller's correlation id for the
    /// translation that fills this same message later.
    /// </summary>
    public static void AppendRecognizedSubtitleFromAnyThread(long captionId, string original, string? speakerLabel = null)
    {
        var window = _subtitle;
        if (window is null) return;
        if (_dispatcherQueue is { } queue)
        {
            queue.TryEnqueue(() => window.AppendRecognizedCaption(captionId, original, speakerLabel));
        }
        else
        {
            window.AppendRecognizedCaption(captionId, original, speakerLabel);
        }
    }

    /// <summary>
    /// Fills one message's translation from any thread. The message keeps its
    /// place in the list; this never appends a second caption.
    /// </summary>
    public static void FillSubtitleTranslationFromAnyThread(long captionId, string? translated)
    {
        var window = _subtitle;
        if (window is null) return;
        if (_dispatcherQueue is { } queue)
        {
            queue.TryEnqueue(() => window.FillCaptionTranslation(captionId, translated));
        }
        else
        {
            window.FillCaptionTranslation(captionId, translated);
        }
    }

    /// <summary>Applies the caption presentation option chosen on the voice page.</summary>
    public static void ApplySubtitleContentMode(SubtitleContentMode mode)
    {
        var window = _subtitle;
        if (window is null) return;
        if (_dispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => window.ApplyContentMode(mode));
        }
        else
        {
            window.ApplyContentMode(mode);
        }
    }

    /// <summary>
    /// Mirrors the recognition session onto the caption surface: stopping
    /// recognition pauses appending and keeps every message already shown, and
    /// the window appears and disappears with the session. Every start/stop path
    /// (shortcut, voice page, shell shutdown) ends here, so recognition can never
    /// keep running behind a hidden window.
    /// </summary>
    private static void OnSubtitleSessionRunningChanged(object? sender, EventArgs e)
    {
        var running = _state?.SubtitleVoice.IsRunning == true;
        void Apply()
        {
            _subtitle?.SetStreamPaused(!running);
            if (running) ShowSubtitle();
            else HideSubtitle();
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess) Apply();
        else _dispatcherQueue.TryEnqueue(Apply);
    }

    public static void Initialize(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_shutdownRequested)
        {
            return;
        }
        if (!ReferenceEquals(_state, state))
        {
            if (_state is not null)
            {
                _state.OverlayAppearance.Changed -= OnAppearanceChanged;
                _state.SubtitleVoice.RunningChanged -= OnSubtitleSessionRunningChanged;
            }

            _state = state;
            _state.OverlayAppearance.Changed += OnAppearanceChanged;
            _state.SubtitleVoice.RunningChanged += OnSubtitleSessionRunningChanged;
        }
        _dispatcherQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        EnsureLayoutStore();
        _ = EnsureQuickInput(state);
        _ = EnsureSubtitle();
    }

    public static void ShowAtStartup(AppState state)
    {
        Initialize(state);
        // 只有输入浮窗在启动时出现。字幕浮窗属于「他人语音 → 字幕」这一个开关，
        // 识别没跑的时候它不留在桌面上（D3）。
        ShowWindow(EnsureQuickInput(state), activate: false);
    }

    public static QuickInputWindow EnsureQuickInput(AppState? state = null)
    {
        if (_shutdownRequested)
        {
            throw new InvalidOperationException("Overlay host has been shut down.");
        }
        if (state is not null) _state = state;
        if (_quickInput is not null) return _quickInput;

        var resolvedState = _state ?? throw new InvalidOperationException("Overlay host is not initialized.");
        EnsureLayoutStore();
        var window = new QuickInputWindow(resolvedState);
        window.Closed += (_, _) =>
        {
            SaveCurrentLayout(window, QuickInputLayoutKey);
            if (ReferenceEquals(_quickInput, window)) _quickInput = null;
        };
        _quickInput = window;
        return window;
    }

    public static SubtitleOverlayWindow EnsureSubtitle()
    {
        if (_shutdownRequested)
        {
            throw new InvalidOperationException("Overlay host has been shut down.");
        }
        if (_subtitle is not null) return _subtitle;

        EnsureLayoutStore();
        var resolvedState = _state ?? throw new InvalidOperationException("Overlay host is not initialized.");
        var window = new SubtitleOverlayWindow(resolvedState.OverlayAppearance.Current.SubtitleOverlayOpacity);
        // Recognition may already be running when the surface is first created.
        window.SetStreamPaused(!resolvedState.SubtitleVoice.IsRunning);
        window.SurfaceCloseRequested += (_, _) => DisableCaptionFromUser();
        // 浮窗只在识别运行时出现：新建出来的窗口先藏起来，等识别启动再显示。
        if (!resolvedState.SubtitleVoice.IsRunning) window.HideOverlay();
        window.Closed += (_, _) =>
        {
            SaveCurrentLayout(window, SubtitleLayoutKey);
            if (ReferenceEquals(_subtitle, window)) _subtitle = null;
        };
        _subtitle = window;
        return window;
    }

    public static void ToggleQuickInput(AppState state)
    {
        var window = EnsureQuickInput(state);
        if (window.IsOverlayVisible && !window.IsOverlayMinimized)
        {
            window.HideOverlay();
            return;
        }

        window.SetPreview(
            state.LastOriginalText,
            state.LastTranslatedText,
            state.LastSecondaryTranslatedText);
        ShowWindow(window, activate: true);
    }

    /// <summary>Whether the caption surface is currently on screen.</summary>
    public static bool IsSubtitleVisible => _subtitle?.IsOverlayVisible == true;

    /// <summary>
    /// Shows the caption surface. The master switch can call this from a worker
    /// thread once recognition started, so the native call is marshalled here.
    /// </summary>
    public static void ShowSubtitle()
    {
        if (_shutdownRequested) return;
        void Apply()
        {
            try
            {
                // The subtitle returns to the topmost band without stealing
                // keyboard focus from VRChat.
                ShowWindow(EnsureSubtitle(), activate: false);
            }
            catch
            {
                // An overlay is optional UI: a failed native show must not stop
                // the recognition session that is already running.
            }
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess) Apply();
        else _dispatcherQueue.TryEnqueue(Apply);
    }

    /// <summary>Hides the caption surface; the next run reuses the same window.</summary>
    public static void HideSubtitle()
    {
        void Apply() => _subtitle?.HideOverlay();
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess) Apply();
        else _dispatcherQueue.TryEnqueue(Apply);
    }

    /// <summary>
    /// The standard Windows close command on the caption window means "stop the
    /// other-player captions", not "hide the window and keep capturing": the
    /// request goes through the same serialized switch as the shortcut.
    /// </summary>
    private static void DisableCaptionFromUser()
    {
        var state = _state;
        if (state is null)
        {
            HideSubtitle();
            return;
        }

        _ = DisableCaptionFromUserAsync(state);
    }

    private static async Task DisableCaptionFromUserAsync(AppState state)
    {
        try
        {
            await state.OtherPlayerCaption.SetEnabledAsync(false);
        }
        catch
        {
            // A stop that reports a failure still has to take the surface down.
            HideSubtitle();
        }
    }

    public static void HideAll()
    {
        _quickInput?.HideOverlay();
        _subtitle?.HideOverlay();
    }

    /// <summary>
    /// Closes both native overlay windows when the desktop shell exits. Hiding
    /// them is insufficient because a top-level Win32 window can keep the
    /// process alive after the main WinUI window has been closed.
    /// </summary>
    public static void CloseAll()
    {
        _shutdownRequested = true;
        var quickInput = _quickInput;
        var subtitle = _subtitle;
        _quickInput = null;
        _subtitle = null;
        if (_state is not null)
        {
            _state.OverlayAppearance.Changed -= OnAppearanceChanged;
            _state.SubtitleVoice.RunningChanged -= OnSubtitleSessionRunningChanged;
        }

        // Capture the final screen rectangles before requesting destruction.
        // Window.Close normally raises Closed synchronously, but the native
        // dispatcher can defer that event while the shell is tearing down. A
        // pre-close snapshot prevents the last drag/resize from being lost in
        // that race; the Closed handlers below remain as a second update path.
        if (quickInput is not null) SaveCurrentLayout(quickInput, QuickInputLayoutKey);
        if (subtitle is not null) SaveCurrentLayout(subtitle, SubtitleLayoutKey);

        CloseWindow(quickInput);
        CloseWindow(subtitle);
        _layoutStore?.Flush();
        _state?.OverlayAppearance.Flush();
    }

    /// <summary>Returns a previously saved rectangle for an overlay kind.</summary>
    public static OverlayWindowLayout? GetSavedLayout(string key)
    {
        EnsureLayoutStore();
        return _layoutStore?.Get(key);
    }

    /// <summary>Queues a layout update from the ordinary window controller.</summary>
    public static void SaveLayout(string key, OverlayWindowLayout layout)
    {
        EnsureLayoutStore();
        _layoutStore?.Set(key, layout);
    }

    private static void EnsureLayoutStore()
    {
        if (_layoutStore is not null) return;
        var path = PortableStorage.GetPath(AppDataFiles.OverlayLayout);
        _layoutStore = new OverlayWindowLayoutStore(path);
    }

    private static void SaveCurrentLayout(QuickInputWindow window, string key)
    {
        if (window.TryGetLayout(out var layout))
        {
            SaveLayout(key, layout);
        }
    }

    private static void SaveCurrentLayout(SubtitleOverlayWindow window, string key)
    {
        if (window.TryGetLayout(out var layout))
        {
            SaveLayout(key, layout);
        }
    }

    private static void CloseWindow(QuickInputWindow? window)
    {
        if (window is null) return;
        try
        {
            window.ClosePermanently();
        }
        catch
        {
            // A window may already have been closed by the user or by the
            // native host. In either case there is nothing left to clean up.
        }
    }

    private static void CloseWindow(SubtitleOverlayWindow? window)
    {
        if (window is null) return;
        try
        {
            window.ClosePermanently();
        }
        catch
        {
            // The native HWND may already have been destroyed.
        }
    }

    private static void ShowWindow(QuickInputWindow window, bool activate)
    {
        try
        {
            window.ShowOverlay(activate);
        }
        catch
        {
            // An overlay is optional UI. A failed native show must not prevent
            // the main application from remaining usable.
        }
    }

    private static void ShowWindow(SubtitleOverlayWindow window, bool activate)
    {
        try
        {
            window.ShowOverlay(activate);
        }
        catch
        {
            // An overlay is optional UI. Keep the main application usable.
        }
    }

    private static void OnAppearanceChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            if (_state is null) return;
            var appearance = _state.OverlayAppearance.Current;
            _quickInput?.ApplyOpacity(appearance.InputOverlayOpacity);
            _subtitle?.ApplyOpacity(appearance.SubtitleOverlayOpacity);
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess) Apply();
        else _dispatcherQueue.TryEnqueue(Apply);
    }
}
