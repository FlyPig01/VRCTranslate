using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Graphics;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Shared chrome for the two game overlays. The overlays are tools that sit on
/// top of VRChat, so a native caption bar wastes useful screen space. The
/// content surface keeps the native resize frame hidden while retaining
/// Windows hit testing, cursors, and pointer capture for move/size gestures.
/// </summary>
internal static class OverlayWindowChrome
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;
    private const uint WmNcLButtonDown = 0x00A1;
    private const nint HtCaption = 2;

    // DWM corner preference is only a fallback. The actual visible shape is
    // supplied by the native window region below, so no rectangular DWM frame
    // is painted around the XAML surface.
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaBorderColor = 34;
    private const int DwmaCaptionColor = 35;
    private const int DwmaNcRenderingPolicy = 2;
    private const int DwmNcRenderingPolicyDisabled = 1;
    private const int DwmcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int IdcArrow = 32512;
    private const int IdcSizeNs = 32645;
    private const int IdcSizeWe = 32644;
    private const int IdcSizeNwse = 32642;
    private const int IdcSizeNesw = 32643;
    private const int GwlWndProc = -4;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmSetCursor = 0x0020;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcPaint = 0x0085;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmSysCommand = 0x0112;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmCaptureChanged = 0x0215;
    private const int VkLButton = 0x01;
    private const nint ScSize = 0xF000;
    private const nint ScMove = 0xF010;
    private const nint HtNowhere = 0;
    private const nint HtClient = 1;
    private const nint HtLeft = 10;
    private const nint HtRight = 11;
    private const nint HtTop = 12;
    private const nint HtTopLeft = 13;
    private const nint HtTopRight = 14;
    private const nint HtBottom = 15;
    private const nint HtBottomLeft = 16;
    private const nint HtBottomRight = 17;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDpiChanged = 0x02E0;

    // Keep each controller alive with its window.  This also keeps the WinRT
    // non-client input source and its native callbacks rooted across hide/show.
    private static readonly List<OverlayInteractionController> Controllers = [];
    private static readonly Dictionary<IntPtr, (int Width, int Height, uint Dpi)> RoundedRegions = [];
    private static readonly Dictionary<IntPtr, int> NativeFrameColors = [];

    public static void Configure(
        Window window,
        UIElement dragSurface,
        int width,
        int height,
        bool alwaysOnTop = true,
        bool fullCaption = false,
        IReadOnlyList<FrameworkElement>? passthroughElements = null,
        OverlayWindowLayout? initialLayout = null,
        Action<OverlayWindowLayout>? layoutChanged = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(dragSurface);

        // Keep a blank title for accessibility/window enumeration without
        // presenting a product name inside the game view.
        window.Title = string.Empty;
        window.ExtendsContentIntoTitleBar = true;
        var surfaceColor = ResolveSurfaceColor(dragSurface);
        var interaction = new OverlayInteractionController(
            window,
            dragSurface,
            fullCaption,
            passthroughElements,
            layoutChanged);
        Controllers.Add(interaction);
        interaction.Attach();

        var hwnd = WindowNative.GetWindowHandle(window);
        if (surfaceColor is { } resolvedSurfaceColor)
        {
            NativeFrameColors[hwnd] = ToColorRef(resolvedSurfaceColor);
        }
        interaction.PrepareNativeFrame(hwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        try
        {
            var titleBar = appWindow.TitleBar;
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
                titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                if (surfaceColor is { } titleBarColor)
                {
                    // A collapsed WinUI title bar can still contribute one
                    // physical row while the HWND is being activated. Match
                    // that row to the overlay instead of allowing the system
                    // light frame to show through above the XAML surface.
                    var opaqueColor = Windows.UI.Color.FromArgb(
                        255,
                        titleBarColor.R,
                        titleBarColor.G,
                        titleBarColor.B);
                    titleBar.BackgroundColor = opaqueColor;
                    titleBar.InactiveBackgroundColor = opaqueColor;
                    titleBar.ButtonBackgroundColor = opaqueColor;
                    titleBar.ButtonInactiveBackgroundColor = opaqueColor;
                }
            }
        }
        catch
        {
            // Older Windows builds can reject title-bar customization. The
            // fallback still has a blank title and a native resizable frame.
        }

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Remove the caption/border before toggling presenter capabilities;
            // WinAppSDK may recreate the native style when these properties
            // are changed after the border state.
            try
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
            catch
            {
                // The Win32 style fallback below still removes the caption.
            }
            presenter.IsAlwaysOnTop = alwaysOnTop;
            // Keep native resizing enabled while the visible title bar remains
            // collapsed. Non-client regions below then use the normal Windows
            // move/size loop and provide the standard resize cursors.
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var targetLayout = ResolveInitialLayout(
            hwnd,
            width,
            height,
            initialLayout);
        appWindow.Resize(new Windows.Graphics.SizeInt32(targetLayout.Width, targetLayout.Height));
        if (initialLayout.HasValue)
        {
            // Position is restored only when a valid saved rectangle exists.
            // Fresh installs retain WinAppSDK's normal placement behavior.
            appWindow.Move(new Windows.Graphics.PointInt32(targetLayout.X, targetLayout.Y));
        }

        // AppWindow.Resize can recreate the overlapped presenter style. Apply
        // the frame changes after the final resize so WS_CAPTION cannot be
        // reintroduced by the presenter and paint a strip around the surface.
        // Remove the caption after WinAppSDK has finished updating the
        // presenter, then apply the native rounded window region.
        if (appWindow.Presenter is OverlappedPresenter resizedPresenter)
        {
            try
            {
                resizedPresenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
            catch
            {
                // The Win32 style fallback below still removes the caption.
            }
        }
        TrySetBorderlessFrame(hwnd);
        TryDisableNativeFrame(hwnd);
        ApplyRoundedWindowRegion(hwnd);
        interaction.ConfigureNativeRegions(hwnd, targetLayout.Width, targetLayout.Height);

        // WinUI may reapply the overlapped presenter style during the first
        // layout pass. Re-apply the borderless style after that pass as well;
        // this removes the transient light strip that otherwise appears above
        // the XAML surface on startup.
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is not null)
        {
            dispatcher.TryEnqueue(() => ApplyNativeChrome(window));
            dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ApplyNativeChrome(window));
        }
    }

    private static OverlayWindowLayout ResolveInitialLayout(
        IntPtr hwnd,
        int requestedWidth,
        int requestedHeight,
        OverlayWindowLayout? savedLayout)
    {
        var width = Math.Clamp(requestedWidth, 240, 10000);
        var height = Math.Clamp(requestedHeight, 56, 10000);
        if (savedLayout is { } saved && saved.IsUsable)
        {
            width = saved.Width;
            height = saved.Height;
        }

        try
        {
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest).WorkArea;
            // Keep a small margin so a saved rectangle from a disconnected or
            // differently scaled monitor cannot make the overlay unreachable.
            var maxWidth = Math.Max(240, workArea.Width - 24);
            var maxHeight = Math.Max(56, workArea.Height - 24);
            width = Math.Clamp(width, 240, Math.Min(10000, maxWidth));
            height = Math.Clamp(height, 56, Math.Min(10000, maxHeight));

            if (savedLayout is { } restored && restored.IsUsable)
            {
                var x = restored.X;
                var y = restored.Y;
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
        }
        catch
        {
            // Display-area lookup is unavailable in design-time and some UI
            // test hosts. The requested dimensions remain a safe fallback.
        }

        // The coordinates are ignored for a fresh window because WinAppSDK
        // chooses its normal placement. They are still populated for a stable
        // callback shape once the native window reports its first rectangle.
        return new OverlayWindowLayout(0, 0, width, height);
    }

    private static void ApplyNativeChrome(Window window)
    {
        try
        {
            var currentHwnd = WindowNative.GetWindowHandle(window);
            if (currentHwnd != IntPtr.Zero)
            {
                TrySetBorderlessFrame(currentHwnd);
                TryDisableNativeFrame(currentHwnd);
                ApplyRoundedWindowRegion(currentHwnd);
            }
        }
        catch
        {
            // Native chrome is cosmetic; keep the overlay usable if a queued
            // callback runs after the window has already been closed.
        }
    }

    /// <summary>
    /// Shows an overlay without stealing focus from the game or the shell.
    /// The first call must activate the WinUI window once so that its HWND is
    /// created; subsequent calls use AppWindow.Show directly.
    /// </summary>
    public static void Show(Window window, bool activate = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var appWindow = GetAppWindow(window);
            appWindow.Show(activate);
        }
        catch
        {
            // A newly constructed Window has no AppWindow until it is
            // activated. Keep this fallback deliberately small and safe.
            window.Activate();
            if (!activate)
            {
                try { GetAppWindow(window).Show(false); } catch { }
            }
        }
    }

    /// <summary>Hides an overlay while keeping its state and event handlers.</summary>
    public static void Hide(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try { GetAppWindow(window).Hide(); }
        catch { }
    }

    /// <summary>Returns the native visibility state used by hotkey toggles.</summary>
    public static bool IsVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the current screen-space rectangle for shutdown flushes.</summary>
    public static bool TryGetLayout(Window window, out OverlayWindowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(window);
        layout = default;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;
            var candidate = new OverlayWindowLayout(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
            if (!candidate.IsUsable) return false;
            layout = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Overlay window handle is not ready.");
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private sealed class OverlayInteractionController
    {
        private const double ResizeGrip = 10;
        private readonly Window _window;
        private readonly FrameworkElement _surface;
        private readonly bool _fullCaption;
        private readonly int _minimumWidth;
        private readonly int _minimumHeight;
        private readonly Action<OverlayWindowLayout>? _layoutChanged;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _cursorTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private readonly WndProcDelegate _wndProc;
        private InputNonClientPointerSource? _nonClientSource;
        private IReadOnlyList<FrameworkElement>? _passthroughElements;
        private RectInt32[] _passthroughRects = [];
        private IntPtr _hwnd;
        private IntPtr _previousWndProc;
        private int _lastClientWidth;
        private int _lastClientHeight;
        private bool _nativeRegionsConfigured;
        private bool _wndProcInstalled;
        private bool _closed;
        private bool _moving;
        private ResizeEdges _resizeEdges;
        private NativePoint _startCursor;
        private NativeRect _startRect;
        private NativePoint _moveStartCursor;
        private NativeRect _moveStartRect;
        private OverlayWindowLayout? _lastPublishedLayout;

        public OverlayInteractionController(
            Window window,
            UIElement surface,
            bool fullCaption,
            IReadOnlyList<FrameworkElement>? passthroughElements = null,
            Action<OverlayWindowLayout>? layoutChanged = null)
        {
            _window = window;
            _surface = surface as FrameworkElement
                ?? throw new ArgumentException("Overlay drag surface must be a FrameworkElement.", nameof(surface));
            _fullCaption = fullCaption;
            _passthroughElements = passthroughElements;
            _layoutChanged = layoutChanged;
            _wndProc = WindowProc;
            _minimumWidth = Math.Max(240, (int)Math.Ceiling(_surface.MinWidth));
            _minimumHeight = Math.Max(56, (int)Math.Ceiling(_surface.MinHeight));
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (_dispatcher is not null)
            {
                _resizeTimer = _dispatcher.CreateTimer();
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _resizeTimer.Tick += (_, _) =>
                {
                    UpdateResize();
                    UpdateMove();
                };
                _cursorTimer = _dispatcher.CreateTimer();
                _cursorTimer.Interval = TimeSpan.FromMilliseconds(50);
                _cursorTimer.Tick += OnCursorTimerTick;
            }
            _window.Closed += OnWindowClosed;
        }

        public void Attach()
        {
            _surface.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnPointerPressed),
                handledEventsToo: true);
            _surface.PointerMoved += OnPointerMoved;
            _surface.PointerExited += OnPointerExited;
            _surface.PointerReleased += OnPointerReleased;
            _surface.PointerCanceled += OnPointerReleased;
            _surface.PointerCaptureLost += OnPointerCaptureLost;
            _cursorTimer?.Start();
        }

        public void PrepareNativeFrame(IntPtr hwnd)
        {
            if (_closed || hwnd == IntPtr.Zero) return;
            _hwnd = hwnd;
            InstallWindowProc(hwnd);
            TrySetBorderlessFrame(hwnd);
            TryDisableNativeFrame(hwnd);
        }

        private void OnCursorTimerTick(
            Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
            object args)
        {
            if (_closed || _hwnd == IntPtr.Zero ||
                !GetWindowRect(_hwnd, out var rect) || !GetCursorPos(out var cursor)) return;
            if (cursor.X < rect.Left || cursor.X >= rect.Right ||
                cursor.Y < rect.Top || cursor.Y >= rect.Bottom) return;

            var hit = HitTest(_hwnd, 0);
            if (IsResizeHit(hit))
            {
                SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)CursorForHit(hit)));
            }
            else if (hit == HtCaption)
            {
                SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow));
            }
        }

        /// <summary>
        /// Registers the real Windows non-client regions. Windows then owns the
        /// move/size modal loop, capture, minimum tracking and resize cursors;
        /// this is the same path used by a normal overlapped window.
        /// </summary>
        public void ConfigureNativeRegions(IntPtr hwnd, int requestedWidth, int requestedHeight)
        {
            if (_closed || hwnd == IntPtr.Zero) return;

            if (_hwnd != IntPtr.Zero && _hwnd != hwnd)
            {
                RestoreWindowProc();
            }
            _hwnd = hwnd;
            InstallWindowProc(hwnd);
            try
            {
                // Cache the current editor bounds even when the WinAppSDK
                // non-client API is unavailable. Initial activation can run
                // before XAML has completed its first measure pass; the hit
                // test also recomputes these bounds on demand below.
                _passthroughRects = BuildPassthroughRects();
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                _nonClientSource ??= InputNonClientPointerSource.GetForWindowId(windowId);
                if (_nonClientSource is null) return;

                if (!GetClientRect(hwnd, out var clientRect))
                {
                    clientRect = new NativeRect { Right = requestedWidth, Bottom = requestedHeight };
                }

                var clientWidth = Math.Max(1, clientRect.Right - clientRect.Left);
                var clientHeight = Math.Max(1, clientRect.Bottom - clientRect.Top);
                _lastClientWidth = clientWidth;
                _lastClientHeight = clientHeight;
                ApplyRoundedWindowRegion(hwnd);

                var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
                var grip = Math.Clamp((int)Math.Round(8d * dpi / 96d), 8, 18);
                grip = Math.Min(grip, Math.Max(4, Math.Min(clientWidth, clientHeight) / 3));

                // Clear stale rectangles after a DPI change or a resize. The
                // caption is set before borders so the border hit regions win
                // at the corners and Windows can select diagonal cursors.
                _nonClientSource.ClearAllRegionRects();
                var captionRects = BuildCaptionRects(clientWidth, clientHeight, grip);
                if (captionRects.Length > 0)
                {
                    _nonClientSource.SetRegionRects(NonClientRegionKind.Caption, captionRects);
                }

                var borderRects = BuildBorderRects(clientWidth, clientHeight, grip);
                _nonClientSource.SetRegionRects(NonClientRegionKind.TopBorder, [borderRects.Top]);
                _nonClientSource.SetRegionRects(NonClientRegionKind.LeftBorder, [borderRects.Left]);
                _nonClientSource.SetRegionRects(NonClientRegionKind.BottomBorder, [borderRects.Bottom]);
                _nonClientSource.SetRegionRects(NonClientRegionKind.RightBorder, [borderRects.Right]);

                var passthrough = _passthroughRects;
                _passthroughRects = passthrough;
                if (passthrough.Length > 0)
                {
                    _nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, passthrough);
                }
                else
                {
                    _nonClientSource.ClearRegionRects(NonClientRegionKind.Passthrough);
                }

                if (!_nativeRegionsConfigured)
                {
                    _nonClientSource.WindowRectChanged += OnNativeWindowRectChanged;
                    _nativeRegionsConfigured = true;
                }

            }
            catch
            {
                // The API is unavailable on a small number of older Windows
                // builds. The XAML pointer fallback remains attached below.
                _nativeRegionsConfigured = false;
            }

            // Publish the rectangle after the HWND and its client regions are
            // ready. This captures the initial system placement as well as a
            // restored placement on hosts that do not emit a later move event.
            PublishLayout(hwnd);
        }

        private void InstallWindowProc(IntPtr hwnd)
        {
            if (_wndProcInstalled || hwnd == IntPtr.Zero) return;
            try
            {
                var callback = Marshal.GetFunctionPointerForDelegate(_wndProc);
                var previous = SetWindowLongPtr(hwnd, GwlWndProc, callback);
                if (previous != IntPtr.Zero)
                {
                    _previousWndProc = previous;
                    _wndProcInstalled = true;
                }
            }
            catch
            {
                // The managed pointer fallback remains available when a host
                // refuses HWND subclassing (for example, a design-time host).
                _wndProcInstalled = false;
            }
        }

        private void RestoreWindowProc()
        {
            if (!_wndProcInstalled || _hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowLongPtr(_hwnd, GwlWndProc, _previousWndProc);
            }
            catch { }
            finally
            {
                _previousWndProc = IntPtr.Zero;
                _wndProcInstalled = false;
            }
        }

        private nint WindowProc(IntPtr hwnd, uint message, nint wParam, nint lParam)
        {
            try
            {
                switch (message)
                {
                    case WmNcCalcSize:
                        // Keep WS_THICKFRAME so USER32 owns the modal resize
                        // loop, but make the entire HWND a XAML client. This
                        // removes the ten-pixel light non-client strip.
                        return 0;

                    case WmNcPaint:
                    case WmEraseBkgnd:
                        // XAML/DWM compose the complete surface. Suppressing
                        // the legacy frame paint prevents a white flash while
                        // the window is being resized or first shown.
                        return 1;

                    case WmNcHitTest:
                    {
                        var hit = HitTest(hwnd, lParam);
                        if (hit != HtNowhere) return hit;
                        break;
                    }

                    case WmSetCursor:
                    {
                        var hit = (nint)(lParam.ToInt64() & 0xffff);
                        // The WinAppSDK non-client source often reports
                        // HTCLIENT here even when the pointer is over a
                        // custom resize strip. Recompute from the physical
                        // cursor so the visible pointer always matches the
                        // edge/corner under it.
                        var physicalHit = HitTest(hwnd, 0);
                        if (IsResizeHit(physicalHit) || physicalHit == HtCaption)
                        {
                            hit = physicalHit;
                        }
                        if (IsResizeHit(hit))
                        {
                            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)CursorForHit(hit)));
                            return 1;
                        }
                        if (hit == HtCaption)
                        {
                            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow));
                            return 1;
                        }
                        break;
                    }

                    case WmNcLButtonDown:
                    {
                        // WinUI's custom-frame procedure can normalize these
                        // messages before USER32 receives them. Preserve the
                        // normal edge hit and use the shared capture path for
                        // caption dragging.
                        var hit = wParam;
                        // InputNonClientPointerSource can normalize a border
                        // press to HTCAPTION before it reaches the subclass.
                        // Re-evaluate the physical pointer so a corner/edge
                        // still enters USER32's resize loop.
                        if (hit == HtCaption)
                        {
                            var corrected = HitTest(hwnd, lParam);
                            if (IsResizeHit(corrected)) hit = corrected;
                        }
                        if (IsResizeHit(hit) || hit == HtCaption)
                        {
                            if (IsResizeHit(hit))
                            {
                                // WinUI's custom non-client source may have
                                // already consumed the native button message.
                                // Queue SC_SIZE after it returns so USER32 can
                                // enter its normal modal resize loop without
                                // re-entering the WinUI procedure.
                                StartNativeResize(hwnd, hit);
                                return 0;
                            }
                            StartNativeMove(hwnd);
                            return 0;
                        }
                        break;
                    }

                    case 0x0201: // WM_LBUTTONDOWN from a custom-frame host
                    {
                        var hit = HitTest(hwnd, lParam);
                        if (IsResizeHit(hit))
                        {
                            StartNativeResize(hwnd, hit);
                            return 0;
                        }
                        if (hit == HtCaption)
                        {
                            StartNativeMove(hwnd);
                            return 0;
                        }
                        break;
                    }

                    case WmLButtonUp:
                    case WmNcLButtonUp:
                    case WmCaptureChanged:
                        EndNativeMove();
                        break;

                    case WmGetMinMaxInfo:
                        ApplyMinimumTrackSize(lParam);
                        break;

                    case WmWindowPosChanged:
                        // Keep the native clip synchronized with a live resize.
                        // The region is the window shape; the XAML surface does
                        // not need a second rounded rectangle to hide a frame.
                        ApplyRoundedWindowRegion(hwnd);
                        PublishLayout(hwnd);
                        break;

                    case WmDpiChanged:
                        ApplyRoundedWindowRegion(hwnd);
                        PublishLayout(hwnd);
                        break;

                    case WmNcDestroy:
                        // Restore USER32's original callback before forwarding
                        // the destroy notification. The delegate stays rooted
                        // until the controller receives Window.Closed.
                        var previousWndProc = _previousWndProc;
                        RestoreWindowProc();
                        return previousWndProc != IntPtr.Zero
                            ? CallWindowProc(previousWndProc, hwnd, message, wParam, lParam)
                            : DefWindowProc(hwnd, message, wParam, lParam);
                }
            }
            catch
            {
                // Never allow an overlay callback exception to tear down the
                // UI thread. USER32's original procedure handles the message.
            }

            return CallPreviousWindowProc(hwnd, message, wParam, lParam);
        }


        private nint HitTest(IntPtr hwnd, nint lParam)
        {
            if (!GetWindowRect(hwnd, out var windowRect)) return HtNowhere;
            var packed = lParam.ToInt64();
            var cursor = new NativePoint
            {
                // WM_NCHITTEST supplies screen coordinates in lParam. Use
                // them directly so synthetic/UIA probes and real mouse input
                // share exactly the same path; fall back to GetCursorPos only
                // for malformed zero messages from unusual hosts.
                X = (short)(packed & 0xffff),
                Y = (short)((packed >> 16) & 0xffff)
            };
            if (packed == 0 && GetCursorPos(out var currentCursor))
            {
                cursor = currentCursor;
            }

            var width = windowRect.Right - windowRect.Left;
            var height = windowRect.Bottom - windowRect.Top;
            if (width <= 0 || height <= 0) return HtNowhere;
            var x = cursor.X - windowRect.Left;
            var y = cursor.Y - windowRect.Top;
            var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
            var grip = Math.Clamp((int)Math.Round(8d * dpi / 96d), 8, 18);
            grip = Math.Min(grip, Math.Max(4, Math.Min(width, height) / 3));

            var left = x >= 0 && x < grip;
            var right = x >= width - grip && x < width;
            var top = y >= 0 && y < grip;
            var bottom = y >= height - grip && y < height;

            if (left && top) return HtTopLeft;
            if (right && top) return HtTopRight;
            if (left && bottom) return HtBottomLeft;
            if (right && bottom) return HtBottomRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;
            if (left) return HtLeft;
            if (right) return HtRight;

            // The text editor must remain a normal client control. Its bounds
            // are cached in physical client pixels after each layout pass.
            foreach (var rect in GetCurrentPassthroughRects())
            {
                if (x >= rect.X && x < rect.X + rect.Width &&
                    y >= rect.Y && y < rect.Y + rect.Height)
                {
                    return HtClient;
                }
            }

            // The subtitle is entirely draggable. The input overlay uses its
            // blank area as a caption while the TextBox remains client-side.
            return HtCaption;
        }

        private IReadOnlyList<RectInt32> GetCurrentPassthroughRects()
        {
            var current = BuildPassthroughRects();
            if (current.Length > 0)
            {
                _passthroughRects = current;
                return current;
            }

            return _passthroughRects;
        }

        private void ApplyMinimumTrackSize(nint lParam)
        {
            if (lParam == 0) return;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, _minimumWidth);
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, _minimumHeight);
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        }

        private static bool IsResizeHit(nint hit) =>
            hit is HtLeft or HtRight or HtTop or HtBottom or
                HtTopLeft or HtTopRight or HtBottomLeft or HtBottomRight;

        private static int CursorForHit(nint hit) => hit switch
        {
            HtLeft or HtRight => IdcSizeWe,
            HtTop or HtBottom => IdcSizeNs,
            HtTopLeft or HtBottomRight => IdcSizeNwse,
            HtTopRight or HtBottomLeft => IdcSizeNesw,
            _ => IdcArrow
        };

        private static nint ResizeCommandForHit(nint hit) => hit switch
        {
            HtLeft => 1,
            HtRight => 2,
            HtTop => 3,
            HtTopLeft => 4,
            HtTopRight => 5,
            HtBottom => 6,
            HtBottomLeft => 7,
            HtBottomRight => 8,
            _ => 0
        };

        private static void StartNativeResize(IntPtr hwnd, nint hit)
        {
            try
            {
                ReleaseCapture();
                var sizeCommand = ScSize | ResizeCommandForHit(hit);
                PostMessage(hwnd, WmSysCommand, sizeCommand, 0);
            }
            catch { }
        }

        private void StartNativeMove(IntPtr hwnd)
        {
            try
            {
                if (!GetCursorPos(out _moveStartCursor) || !GetWindowRect(hwnd, out _moveStartRect)) return;
                _moving = true;
                _resizeTimer?.Start();
                SetCapture(hwnd);
            }
            catch { }
        }

        private void UpdateMove()
        {
            if (!_moving || _hwnd == IntPtr.Zero) return;
            try
            {
                // Polling the button state avoids relying on whether the
                // WinAppSDK non-client source forwards WM_MOUSEMOVE while a
                // caption region is captured.
                if (GetAsyncKeyState(VkLButton) >= 0)
                {
                    EndNativeMove();
                    return;
                }
                if (!GetCursorPos(out var cursor)) return;
                var dx = cursor.X - _moveStartCursor.X;
                var dy = cursor.Y - _moveStartCursor.Y;
                _ = SetWindowPos(
                    _hwnd,
                    IntPtr.Zero,
                    _moveStartRect.Left + dx,
                    _moveStartRect.Top + dy,
                    _moveStartRect.Right - _moveStartRect.Left,
                    _moveStartRect.Bottom - _moveStartRect.Top,
                    SwpNoZOrder | SwpNoActivate);
            }
            catch
            {
                EndNativeMove();
            }
        }

        private void EndNativeMove()
        {
            if (!_moving) return;
            _moving = false;
            try { ReleaseCapture(); }
            catch { }
            if (_resizeEdges == ResizeEdges.None)
            {
                try { _resizeTimer?.Stop(); }
                catch { }
            }
        }

        private nint CallPreviousWindowProc(IntPtr hwnd, uint message, nint wParam, nint lParam)
        {
            if (_previousWndProc != IntPtr.Zero)
            {
                return CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam);
            }

            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private void OnNativeWindowRectChanged(InputNonClientPointerSource sender, WindowRectChangedEventArgs args)
        {
            if (_closed || _dispatcher is null) return;
            PublishLayout(_hwnd);
            // Refresh after the native move-size loop has committed its new
            // client rectangle. Coalesce repeated WM_WINDOWPOSCHANGED events.
            _dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (_closed || _hwnd == IntPtr.Zero) return;
                    if (!GetClientRect(_hwnd, out var rect)) return;
                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width == _lastClientWidth && height == _lastClientHeight) return;
                    ConfigureNativeRegions(_hwnd, width, height);
                });
        }

        private void PublishLayout(IntPtr hwnd)
        {
            if (_closed || _layoutChanged is null || hwnd == IntPtr.Zero) return;
            try
            {
                if (!GetWindowRect(hwnd, out var rect)) return;
                var layout = new OverlayWindowLayout(
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top);
                if (!layout.IsUsable || _lastPublishedLayout == layout) return;
                _lastPublishedLayout = layout;
                _layoutChanged(layout);
            }
            catch
            {
                // Layout persistence is best effort and must never interrupt
                // native move/resize handling.
            }
        }

        private RectInt32[] BuildCaptionRects(int width, int height, int grip)
        {
            if (width <= grip * 2 || height <= grip * 2) return [];

            if (_fullCaption)
            {
                return [new RectInt32(grip, grip, width - grip * 2, height - grip * 2)];
            }

            // The input TextBox is registered as a passthrough region. Keep a
            // compact caption band above/below it and along the sides so the
            // entire overlay remains easy to reposition without stealing text
            // focus from the editor.
            var band = Math.Clamp((int)Math.Round(18d * Math.Max(96u, GetDpiForWindow(_hwnd)) / 96d), 14, 30);
            band = Math.Min(band, Math.Max(8, height - grip * 2));
            var rects = new List<RectInt32>
            {
                new(grip, grip, width - grip * 2, band),
                new(grip, Math.Max(grip, height - grip - band), width - grip * 2, band)
            };
            return rects.ToArray();
        }

        private static BorderRects BuildBorderRects(int width, int height, int grip)
        {
            var innerWidth = Math.Max(1, width - grip);
            var innerHeight = Math.Max(1, height - grip);
            return new BorderRects(
                new RectInt32(0, 0, width, grip),
                new RectInt32(0, grip, grip, innerHeight),
                new RectInt32(0, Math.Max(0, height - grip), width, grip),
                new RectInt32(Math.Max(0, width - grip), grip, grip, innerHeight));
        }

        private RectInt32[] BuildPassthroughRects()
        {
            if (_passthroughElements is null || _passthroughElements.Count == 0) return [];
            var dpi = Math.Max(96u, GetDpiForWindow(_hwnd));
            var scale = dpi / 96d;
            var rects = new List<RectInt32>();
            foreach (var element in _passthroughElements)
            {
                try
                {
                    if (element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                    var transform = element.TransformToVisual(_surface);
                    var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    var x = (int)Math.Floor(origin.X * scale);
                    var y = (int)Math.Floor(origin.Y * scale);
                    var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
                    var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
                    rects.Add(new RectInt32(x, y, w, h));
                }
                catch
                {
                    // Layout may not have produced a transform yet. A later
                    // window-rect/layout pass will register it again.
                }
            }
            return rects.ToArray();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(_surface);
            if (!point.Properties.IsLeftButtonPressed) return;

            var edges = ResolveResizeEdges(point.Position.X, point.Position.Y);
            // WinUI can route a pointer from a border region through the
            // client tree even when the HWND hit test reported a resize edge
            // (notably after the native region is applied). Keep the same pointer
            // capture fallback available in that case. Native USER32 owns
            // the usual path; this branch only handles the client-routed
            // pointer and preserves the same edge geometry/minimum sizes.
            if (edges != ResizeEdges.None && BeginResize(edges, args))
            {
                SetResizeCursor(edges);
                args.Handled = true;
                return;
            }

            if (IsInteractiveSource(args.OriginalSource as DependencyObject)) return;

            try
            {
                var hwnd = WindowNative.GetWindowHandle(_window);
                ReleaseCapture();
                _ = SendMessage(hwnd, WmNcLButtonDown, HtCaption, 0);
                args.Handled = true;
            }
            catch
            {
                // A drag gesture must never interrupt typing in the overlay.
            }
        }

        private bool BeginResize(ResizeEdges edges, PointerRoutedEventArgs args)
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(_window);
                if (hwnd == IntPtr.Zero || !GetCursorPos(out _startCursor) || !GetWindowRect(hwnd, out _startRect))
                    return false;

                _resizeEdges = edges;
                var captured = _surface.CapturePointer(args.Pointer);
                if (captured)
                {
                    _resizeTimer?.Start();
                }
                else
                {
                    StopResize();
                }
                return captured;
            }
            catch
            {
                _resizeEdges = ResizeEdges.None;
                return false;
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (_resizeEdges != ResizeEdges.None)
            {
                UpdateResize();
                args.Handled = true;
                return;
            }

            // This path is only used when non-client registration is not
            // available. Keep the cursor consistent with native Windows.
            if (!_wndProcInstalled && !_nativeRegionsConfigured)
            {
                SetResizeCursor(ResolveResizeEdges(
                    args.GetCurrentPoint(_surface).Position.X,
                    args.GetCurrentPoint(_surface).Position.Y));
            }
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs args)
        {
            if (_resizeEdges == ResizeEdges.None) SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow));
        }

        private void UpdateResize()
        {
            if (_resizeEdges == ResizeEdges.None || !GetCursorPos(out var cursor)) return;

            var deltaX = cursor.X - _startCursor.X;
            var deltaY = cursor.Y - _startCursor.Y;
            var left = _startRect.Left;
            var top = _startRect.Top;
            var right = _startRect.Right;
            var bottom = _startRect.Bottom;

            if (_resizeEdges.HasFlag(ResizeEdges.Left)) left += deltaX;
            if (_resizeEdges.HasFlag(ResizeEdges.Top)) top += deltaY;
            if (_resizeEdges.HasFlag(ResizeEdges.Right)) right += deltaX;
            if (_resizeEdges.HasFlag(ResizeEdges.Bottom)) bottom += deltaY;

            if (right - left < _minimumWidth)
            {
                if (_resizeEdges.HasFlag(ResizeEdges.Left)) left = right - _minimumWidth;
                else right = left + _minimumWidth;
            }
            if (bottom - top < _minimumHeight)
            {
                if (_resizeEdges.HasFlag(ResizeEdges.Top)) top = bottom - _minimumHeight;
                else bottom = top + _minimumHeight;
            }

            try
            {
                var hwnd = WindowNative.GetWindowHandle(_window);
                _ = SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    left,
                    top,
                    right - left,
                    bottom - top,
                    SwpNoZOrder | SwpNoActivate);
            }
            catch
            {
                StopResize();
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (_resizeEdges == ResizeEdges.None) return;
            EndResize(args.Pointer);
            args.Handled = true;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args) =>
            StopResize();

        private void EndResize(Microsoft.UI.Xaml.Input.Pointer pointer)
        {
            StopResize();
            try { _surface.ReleasePointerCapture(pointer); }
            catch { }
        }

        private void StopResize()
        {
            _resizeEdges = ResizeEdges.None;
            try { _resizeTimer?.Stop(); }
            catch { }
        }

        private ResizeEdges ResolveResizeEdges(double x, double y)
        {
            var edges = ResizeEdges.None;
            if (x <= ResizeGrip) edges |= ResizeEdges.Left;
            else if (x >= _surface.ActualWidth - ResizeGrip) edges |= ResizeEdges.Right;
            if (y <= ResizeGrip) edges |= ResizeEdges.Top;
            else if (y >= _surface.ActualHeight - ResizeGrip) edges |= ResizeEdges.Bottom;
            return edges;
        }

        private static void SetResizeCursor(ResizeEdges edges)
        {
            var cursor = edges switch
            {
                ResizeEdges.Left or ResizeEdges.Right => IdcSizeWe,
                ResizeEdges.Top or ResizeEdges.Bottom => IdcSizeNs,
                ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => IdcSizeNwse,
                ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom => IdcSizeNesw,
                _ => IdcArrow
            };
            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)cursor));
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _closed = true;
            StopResize();
            EndNativeMove();
            try
            {
                _cursorTimer?.Stop();
                _cursorTimer!.Tick -= OnCursorTimerTick;
            }
            catch { }
            RestoreWindowProc();
            try
            {
                if (_nonClientSource is not null)
                {
                    _nonClientSource.WindowRectChanged -= OnNativeWindowRectChanged;
                    _nonClientSource.ClearAllRegionRects();
                }
            }
            catch { }
            _passthroughRects = [];
            _lastPublishedLayout = null;
            if (_hwnd != IntPtr.Zero)
            {
                RoundedRegions.Remove(_hwnd);
                NativeFrameColors.Remove(_hwnd);
            }
            Controllers.Remove(this);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WndProcDelegate(
            IntPtr hwnd,
            uint message,
            nint wParam,
            nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        private readonly record struct BorderRects(
            RectInt32 Top,
            RectInt32 Left,
            RectInt32 Bottom,
            RectInt32 Right);

    }

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or ComboBox or Button or ToggleButton or Slider or PasswordBox)
            {
                return true;
            }
        }

        return false;
    }

    private static Windows.UI.Color? ResolveSurfaceColor(UIElement surface)
    {
        if (surface is not Panel { Background: SolidColorBrush brush }) return null;

        // The XAML surface is translucent. DWM's one-pixel resize border is
        // opaque, so blend the surface over WinUI's white composition base to
        // obtain the same visible colour used by the client pixels.
        var source = brush.Color;
        var alpha = Math.Clamp((source.A / 255d) * surface.Opacity, 0d, 1d);
        byte Blend(byte channel) =>
            (byte)Math.Clamp((int)Math.Round(channel * alpha + 255d * (1d - alpha)), 0, 255);
        return Windows.UI.Color.FromArgb(255, Blend(source.R), Blend(source.G), Blend(source.B));
    }

    private static int ToColorRef(Windows.UI.Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    private static void TrySetBorderlessFrame(IntPtr hwnd)
    {
        try
        {
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            // Keep WS_THICKFRAME so USER32 provides the normal move/size loop
            // and resize cursors. WM_NCCALCSIZE below removes its visible
            // non-client pixels, while the caption/system menu bits stay off.
            style &= ~(WsCaption | WsSysMenu);
            style |= WsThickFrame;
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            // Presenter-level border removal remains as a fallback.
        }
    }

    private static void TryDisableNativeFrame(IntPtr hwnd)
    {
        try
        {
            // The HWND region below owns the rounded shape. Keep DWM from
            // applying a second corner mask, which can leave dark wedges at
            // the four corners of a translucent overlay.
            var round = DwmcpDoNotRound;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmaWindowCornerPreference,
                ref round,
                sizeof(int));

            // WS_THICKFRAME is retained for the normal Windows resize loop,
            // but Windows 11 otherwise draws its default light frame around
            // that style. Hide that frame at the source instead of covering it
            // with another XAML rounded rectangle.
            var borderColor = NativeFrameColors.TryGetValue(hwnd, out var surfaceColor)
                ? surfaceColor
                : DwmColorNone;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmaBorderColor,
                ref borderColor,
                sizeof(int));

            if (borderColor != DwmColorNone)
            {
                var captionColor = borderColor;
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmaCaptionColor,
                    ref captionColor,
                    sizeof(int));
            }

            // Disable DWM non-client rendering completely. The XAML surface
            // and the native rounded region are the only visible layers; this
            // avoids a rectangular light frame outside the rounded surface.
            var ncPolicy = DwmNcRenderingPolicyDisabled;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmaNcRenderingPolicy,
                ref ncPolicy,
                sizeof(int));

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            // DWM attributes are unavailable on older Windows builds. The
            // title-bar collapse and presenter resize settings remain valid.
        }
    }

    private static void ApplyRoundedWindowRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
            if (RoundedRegions.TryGetValue(hwnd, out var applied) &&
                applied.Width == width && applied.Height == height &&
                applied.Dpi == dpi)
            {
                return;
            }

            var radius = Math.Clamp((int)Math.Round(10d * dpi / 96d), 8, 24);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region == IntPtr.Zero) return;
            // Mark the dimensions before SetWindowRgn sends WM_WINDOWPOSCHANGED;
            // the subclass then sees the same dimensions and avoids recursively
            // replacing the region during one resize transaction.
            RoundedRegions[hwnd] = (width, height, dpi);
            if (SetWindowRgn(hwnd, region, true) == 0)
            {
                RoundedRegions.Remove(hwnd);
                DeleteObject(region);
            }
            // SetWindowRgn transfers ownership of a successful region to the
            // window; do not delete it on the success path.
        }
        catch
        {
            // The DWM rounded-corner preference remains the fallback on hosts
            // where user32 region APIs are unavailable.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(
        IntPtr previousWndProc,
        IntPtr hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(
        IntPtr hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(
        IntPtr hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr cursorId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
