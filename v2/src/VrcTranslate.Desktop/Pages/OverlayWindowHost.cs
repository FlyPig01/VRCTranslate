using Microsoft.UI.Xaml;
using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Configuration;

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
    private static bool _shutdownRequested;

    public const string QuickInputLayoutKey = "quick-input";
    public const string SubtitleLayoutKey = "subtitle";

    public static QuickInputWindow? QuickInput => _quickInput;

    public static SubtitleOverlayWindow? Subtitle => _subtitle;

    public static void Initialize(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_shutdownRequested)
        {
            return;
        }
        _state = state;
        EnsureLayoutStore();
        _ = EnsureQuickInput(state);
        _ = EnsureSubtitle();
    }

    public static void ShowAtStartup(AppState state)
    {
        Initialize(state);
        ShowWindow(EnsureQuickInput(state), activate: false);
        ShowWindow(EnsureSubtitle(), activate: false);
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
        var window = new SubtitleOverlayWindow();
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
        if (OverlayWindowChrome.IsVisible(window))
        {
            OverlayWindowChrome.Hide(window);
            return;
        }

        window.SetPreview(
            state.LastOriginalText,
            state.LastTranslatedText,
            state.LastSecondaryTranslatedText);
        ShowWindow(window, activate: true);
    }

    public static void ShowSubtitle()
    {
        ShowWindow(EnsureSubtitle(), activate: false);
    }

    /// <summary>Shows or hides the subtitle overlay without changing recognition state.</summary>
    public static void ToggleSubtitle()
    {
        var window = EnsureSubtitle();
        if (OverlayWindowChrome.IsVisible(window))
        {
            OverlayWindowChrome.Hide(window);
            return;
        }

        ShowWindow(window, activate: false);
    }

    public static void HideAll()
    {
        if (_quickInput is not null) OverlayWindowChrome.Hide(_quickInput);
        if (_subtitle is not null) OverlayWindowChrome.Hide(_subtitle);
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
    }

    /// <summary>Returns a previously saved rectangle for an overlay kind.</summary>
    public static OverlayWindowLayout? GetSavedLayout(string key)
    {
        EnsureLayoutStore();
        return _layoutStore?.Get(key);
    }

    /// <summary>Queues a layout update from the native chrome controller.</summary>
    public static void SaveLayout(string key, OverlayWindowLayout layout)
    {
        EnsureLayoutStore();
        _layoutStore?.Set(key, layout);
    }

    private static void EnsureLayoutStore()
    {
        if (_layoutStore is not null) return;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCTranslate",
            "v2-overlay-layout.json");
        _layoutStore = new OverlayWindowLayoutStore(path);
    }

    private static void SaveCurrentLayout(Window window, string key)
    {
        if (OverlayWindowChrome.TryGetLayout(window, out var layout))
        {
            SaveLayout(key, layout);
        }
    }

    private static void CloseWindow(Window? window)
    {
        if (window is null) return;
        try
        {
            window.Close();
        }
        catch
        {
            // A window may already have been closed by the user or by the
            // native host. In either case there is nothing left to clean up.
        }
    }

    private static void ShowWindow(Window window, bool activate)
    {
        try
        {
            if (!OverlayWindowChrome.IsVisible(window))
            {
                // Activate once to create the native HWND and run the window's
                // one-time chrome setup. Show(false) immediately restores the
                // shell/game focus for startup overlays.
                window.Activate();
            }

            OverlayWindowChrome.Show(window, activate);
        }
        catch
        {
            // An overlay is optional UI. A failed native show must not prevent
            // the main application from remaining usable.
        }
    }
}
