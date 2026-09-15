using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Graphics;
using Windows.UI;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Owns the native presentation of one overlay. Movement, resizing, cursors,
/// snapping and DPI transitions deliberately remain with the standard
/// overlapped Windows presenter.
/// </summary>
internal sealed class OverlayWindowController : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly Action<OverlayWindowLayout>? _layoutChanged;
    private readonly Action? _userCloseRequested;
    private bool _closingPermanently;
    private bool _disposed;

    public OverlayWindowController(
        Window window,
        string title,
        int defaultWidth,
        int defaultHeight,
        OverlayWindowLayout? initialLayout,
        Action<OverlayWindowLayout>? layoutChanged,
        double opacity,
        Action? userCloseRequested = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        _window = window;
        _layoutChanged = layoutChanged;
        _userCloseRequested = userCloseRequested;
        _window.Title = title;
        _window.ExtendsContentIntoTitleBar = false;

        _hwnd = WindowNative.GetWindowHandle(window);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Overlay window handle is not available.");
        }

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        // The HWND and AppWindow are the only hard requirements. Older
        // Windows/App SDK combinations may reject one optional presentation
        // API; each decoration therefore degrades independently while the
        // standard Window remains openable and usable.
        BestEffort(() => ConfigurePresenter(_appWindow));
        BestEffort(() => ConfigureTitleBar(_appWindow, title));
        BestEffort(() => ConfigureIcon(_appWindow));
        BestEffort(() => RestoreLayout(_appWindow, defaultWidth, defaultHeight, initialLayout));
        BestEffort(() => ApplyOpacity(opacity));

        BestEffort(() => _appWindow.Changed += OnAppWindowChanged);
        BestEffort(() => _appWindow.Closing += OnAppWindowClosing);
        BestEffort(() => _window.Closed += OnWindowClosed);
    }

    public bool IsVisible => !_disposed && IsWindowVisible(_hwnd);


    public bool IsMinimized => !_disposed && IsIconic(_hwnd);

    public static bool IsFallbackVisible(Window window)
    {
        try
        {
            return IsWindowVisible(WindowNative.GetWindowHandle(window));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFallbackMinimized(Window window)
    {
        try
        {
            return IsIconic(WindowNative.GetWindowHandle(window));
        }
        catch
        {
            return false;
        }
    }

    public static void ShowFallback(Window window, bool activate)
    {
        if (activate)
        {
            window.Activate();
            return;
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero || !ShowWindow(hwnd, SwShowNoActivate))
            {
                window.Activate();
            }
        }
        catch
        {
            // Showing the surface is more important than preserving focus on
            // a platform that cannot expose its native handle.
            window.Activate();
        }
    }

    public static void HideFallback(Window window)
    {
        try
        {
            _ = ShowWindow(WindowNative.GetWindowHandle(window), SwHide);
        }
        catch
        {
            // A missing native handle already means there is no visible HWND
            // to hide through this compatibility path.
        }
    }

    /// <summary>Shows the existing HWND, optionally giving the editor focus.</summary>
    public void Show(bool activate)
    {
        if (_disposed) return;

        if (IsMinimized)
        {
            if (activate)
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Restore();
                }

                _appWindow.Show(true);
            }
            else
            {
                // AppWindow.Show(false) leaves an already-visible minimized
                // HWND iconic. SW_SHOWNOACTIVATE restores its last rectangle
                // without taking focus from the game/current foreground app.
                _ = ShowWindow(_hwnd, SwShowNoActivate);
            }

            return;
        }

        _appWindow.Show(activate);
    }

    /// <summary>Hides the existing HWND so the next shortcut reuses it.</summary>
    public void Hide()
    {
        if (_disposed) return;
        _appWindow.Hide();
    }

    public void ApplyOpacity(double opacity)
    {
        if (_disposed) return;
        var normalized = OverlayAppearanceSettings.NormalizeOpacity(opacity);
        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExLayered) == 0)
        {
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle | WsExLayered));
        }

        var alpha = (byte)Math.Clamp((int)Math.Round(normalized * byte.MaxValue), 0, byte.MaxValue);
        // Opacity is an appearance preference. A transient platform failure
        // must not take down translation or the settings page.
        _ = SetLayeredWindowAttributes(_hwnd, 0, alpha, LwaAlpha);
    }

    public bool TryGetLayout(out OverlayWindowLayout layout)
    {
        layout = default;
        if (_disposed) return false;

        var position = _appWindow.Position;
        var size = _appWindow.Size;
        var candidate = new OverlayWindowLayout(position.X, position.Y, size.Width, size.Height);
        if (!candidate.IsUsable) return false;

        layout = candidate;
        return true;
    }

    /// <summary>
    /// Resizes the surface to a content-following height while keeping its top
    /// edge where the reader put it: the caption strip grows and shrinks
    /// downwards, the same direction its top-aligned messages grow, so the
    /// captions already on screen stay in place instead of travelling up as a
    /// message below them arrives. Returns false when the height is already the
    /// requested one, when growing to it would leave the display, or when the
    /// native call could not be made.
    /// </summary>
    public bool ResizeKeepingTop(int height) => ResizeKeepingTop(0, height);

    /// <summary>
    /// Same resize with an explicit physical width; zero keeps the current one.
    /// A fresh install uses it to turn the default DIP size into the window's own
    /// pixels for the present display scale, so the editor strip measures the same
    /// on a high-DPI screen as it does at 100%.
    /// </summary>
    public bool ResizeKeepingTop(int width, int height)
    {
        if (_disposed) return false;
        var targetWidth = width > 0 ? Math.Clamp(width, 240, 10000) : 0;
        var targetHeight = Math.Clamp(height, 56, 10000);
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (size.Height == targetHeight && (targetWidth == 0 || size.Width == targetWidth)) return false;

        try
        {
            var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            // Growing downwards must not push the caption strip off the bottom of
            // the display. When the space below the top edge is not enough, the
            // surface keeps the height it has and the newest message stays
            // reachable by scrolling, exactly as it is before the first fit.
            if (position.Y + targetHeight > workArea.Y + workArea.Height) return false;
            // The same rule sideways: a default wider than the display keeps a
            // rectangle the reader can still reach. Only the fresh-install path
            // passes a width, so this clamp can never fight the reader's own
            // resize - that one goes straight to the window.
            targetWidth = targetWidth > 0 ? Math.Min(targetWidth, Math.Max(240, workArea.Width - 24)) : 0;
        }
        catch
        {
            // A headless or design-time host has no display area; apply the size.
        }

        try
        {
            _appWindow.MoveAndResize(new RectInt32(
                position.X,
                position.Y,
                targetWidth > 0 ? targetWidth : size.Width,
                targetHeight));
        }
        catch
        {
            // Sizing follows the content; a rejected resize must never surface as
            // an error to the reader.
            return false;
        }

        return true;
    }

    /// <summary>Allows the real close only during application shutdown.</summary>
    public void ClosePermanently()
    {
        if (_disposed) return;
        PublishLayout();
        _closingPermanently = true;
        _window.Close();
    }

    private static void ConfigurePresenter(AppWindow appWindow)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter)
        {
            presenter = OverlappedPresenter.Create();
            appWindow.SetPresenter(presenter);
        }

        BestEffort(presenter.Restore);
        BestEffort(() => presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true));
        BestEffort(() => presenter.IsAlwaysOnTop = true);
        BestEffort(() => presenter.IsResizable = true);
        BestEffort(() => presenter.IsMaximizable = true);
        BestEffort(() => presenter.IsMinimizable = true);
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Optional presentation APIs must not disable the core overlay.
        }
    }

    private static void ConfigureTitleBar(AppWindow appWindow, string title)
    {
        appWindow.Title = title;
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var titleBar = appWindow.TitleBar;
        var background = Color.FromArgb(255, 15, 25, 39);
        var inactiveBackground = Color.FromArgb(255, 20, 30, 44);
        var foreground = Colors.White;
        var inactiveForeground = Color.FromArgb(255, 178, 190, 207);
        var hover = Color.FromArgb(255, 39, 55, 75);
        var pressed = Color.FromArgb(255, 51, 70, 94);

        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = inactiveBackground;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = inactiveBackground;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static void ConfigureIcon(AppWindow appWindow)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
    }

    private static void RestoreLayout(
        AppWindow appWindow,
        int defaultWidth,
        int defaultHeight,
        OverlayWindowLayout? initialLayout)
    {
        var defaultSize = ClampSizeToWorkArea(appWindow.Id, defaultWidth, defaultHeight);
        if (initialLayout is not { } saved || !saved.IsUsable)
        {
            appWindow.Resize(defaultSize);
            return;
        }

        var restored = KeepReachable(appWindow.Id, saved);
        appWindow.MoveAndResize(
            new RectInt32(restored.X, restored.Y, restored.Width, restored.Height));
    }

    private static SizeInt32 ClampSizeToWorkArea(WindowId windowId, int width, int height)
    {
        var safeWidth = Math.Clamp(width, 240, 10000);
        var safeHeight = Math.Clamp(height, 56, 10000);
        try
        {
            var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest).WorkArea;
            safeWidth = Math.Min(safeWidth, Math.Max(240, workArea.Width - 24));
            safeHeight = Math.Min(safeHeight, Math.Max(56, workArea.Height - 24));
        }
        catch
        {
            // Design-time and headless hosts may not expose a display area.
        }

        return new SizeInt32(safeWidth, safeHeight);
    }

    private static OverlayWindowLayout KeepReachable(WindowId windowId, OverlayWindowLayout layout)
    {
        try
        {
            // A newly-created HWND normally starts on the primary display.
            // Resolving from that HWND would incorrectly pull a saved window
            // off a still-connected secondary display. Prefer the display
            // containing the saved rectangle; only use the new HWND as a
            // fallback when that rectangle no longer intersects any monitor.
            var savedRectangle = new RectInt32(layout.X, layout.Y, layout.Width, layout.Height);
            var savedDisplay = DisplayArea.GetFromRect(savedRectangle, DisplayAreaFallback.None);
            var targetDisplay = savedDisplay ??
                                DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            var workArea = targetDisplay.WorkArea;
            var width = Math.Min(layout.Width, Math.Max(240, workArea.Width - 24));
            var height = Math.Min(layout.Height, Math.Max(56, workArea.Height - 24));
            var x = layout.X;
            var y = layout.Y;
            const int visiblePixels = 64;

            if (x + visiblePixels > workArea.X + workArea.Width ||
                x + width - visiblePixels < workArea.X)
            {
                x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            }

            if (y + visiblePixels > workArea.Y + workArea.Height ||
                y + height - visiblePixels < workArea.Y)
            {
                y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            }

            return new OverlayWindowLayout(x, y, width, height);
        }
        catch
        {
            return layout;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            PublishLayout();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        PublishLayout();
        if (_closingPermanently) return;

        args.Cancel = true;
        if (_userCloseRequested is not null)
        {
            // The owner decides what closing this window means. The caption
            // surface treats it as turning the whole feature off, so the window
            // may stay up for the moment that serialized transition takes.
            _userCloseRequested();
            return;
        }

        Hide();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    private void PublishLayout()
    {
        if (TryGetLayout(out var layout)) _layoutChanged?.Invoke(layout);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Closing -= OnAppWindowClosing;
        _window.Closed -= OnWindowClosed;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int newLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, newLong)
            : new IntPtr(SetWindowLong32(hWnd, index, newLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hWnd,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int showCommand);
}
