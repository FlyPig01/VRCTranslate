param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\artifacts\manual-test\VrcTranslate.exe'),
    [string]$OutputDirectory = (Join-Path $env:TEMP 'VRCTranslate-release-overlay-smoke')
)

$ErrorActionPreference = 'Stop'

$executable = (Resolve-Path $ExecutablePath -ErrorAction Stop).Path
$packageDirectory = Split-Path $executable -Parent
$requiredAssets = @(
    'app.ico',
    'VrcTranslate.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Resources\default-glossary.json',
    'Pages\GuidePage.xbf',
    'Pages\RunPage.xbf',
    'Pages\SelfMessagePage.xbf',
    'Pages\SettingsPage.xbf',
    'Pages\SubtitleOverlayWindow.xbf',
    'Pages\TranslationPage.xbf',
    'Pages\VoicePage.xbf'
)
foreach ($asset in $requiredAssets) {
    $assetPath = Join-Path $packageDirectory $asset
    if (-not (Test-Path $assetPath -PathType Leaf)) {
        throw "Release package is missing required asset: $asset"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputDirectory = (Resolve-Path $OutputDirectory).Path
$quickCapturePath = Join-Path $outputDirectory 'quick-input.png'
$subtitleCapturePath = Join-Path $outputDirectory 'subtitle.png'
Remove-Item $quickCapturePath, $subtitleCapturePath -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$drawingAssembly = [System.Drawing.Bitmap].Assembly.Location
$drawingReferences = @($drawingAssembly)
$gdiPlusAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.GdiPlus.dll'
$windowsCoreAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.Core.dll'
if (Test-Path $gdiPlusAssembly) { $drawingReferences += $gdiPlusAssembly }
if (Test-Path $windowsCoreAssembly) { $drawingReferences += $windowsCoreAssembly }

Add-Type -ReferencedAssemblies $drawingReferences @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ReleaseOverlayVisualNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    public static bool HasRoundedWindowRegion(IntPtr hwnd)
    {
        IntPtr region = CreateRectRgn(0, 0, 1, 1);
        if (region == IntPtr.Zero) return false;
        try
        {
            if (GetWindowRgn(hwnd, region) == 0) return false;
            Rect rect;
            if (!GetWindowRect(hwnd, out rect)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 24 || height < 24) return false;
            var cornersOutside =
                !PtInRegion(region, 1, 1) &&
                !PtInRegion(region, width - 2, 1) &&
                !PtInRegion(region, 1, height - 2) &&
                !PtInRegion(region, width - 2, height - 2);
            var centerInside = PtInRegion(region, width / 2, height / 2);
            var edgeCentersInside =
                PtInRegion(region, width / 2, 1) &&
                PtInRegion(region, 1, height / 2) &&
                PtInRegion(region, width - 2, height / 2) &&
                PtInRegion(region, width / 2, height - 2);
            return cornersOutside && centerInside && edgeCentersInside;
        }
        finally { DeleteObject(region); }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private const uint KeyUp = 2;

    public static void TapKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(180);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
    }

    public static void TapGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            throw new ArgumentException("Hotkey gesture is required.", "gesture");

        var parts = gesture.ToUpperInvariant().Replace(" ", string.Empty).Split('+');
        byte virtualKey = 0;
        var modifiers = new System.Collections.Generic.List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case "CTRL":
                case "CONTROL": modifiers.Add(0x11); break;
                case "ALT":
                case "MENU": modifiers.Add(0x12); break;
                case "SHIFT": modifiers.Add(0x10); break;
                case "WIN":
                case "WINDOWS": modifiers.Add(0x5B); break;
                default:
                    if (part.Length == 1) virtualKey = (byte)part[0];
                    else if (part.Length >= 2 && part[0] == 'F')
                    {
                        byte functionKey;
                        if (byte.TryParse(part.Substring(1), out functionKey) &&
                            functionKey >= 1 && functionKey <= 12)
                            virtualKey = (byte)(0x6F + functionKey);
                    }
                    break;
            }
        }

        if (virtualKey == 0)
            throw new ArgumentException("Unsupported hotkey gesture: " + gesture, "gesture");

        foreach (var modifier in modifiers)
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(120);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(180);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
        for (var index = modifiers.Count - 1; index >= 0; index--)
            keybd_event(modifiers[index], 0, KeyUp, UIntPtr.Zero);
    }

    public static string Capture(IntPtr hwnd, string path)
    {
        Rect rect;
        if (!GetWindowRect(hwnd, out rect))
            throw new InvalidOperationException("GetWindowRect failed.");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException(string.Format("Invalid window bounds: {0}x{1}.", width, height));

        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, 2))
                        throw new InvalidOperationException("PrintWindow failed.");
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            bitmap.Save(path, ImageFormat.Png);
        }
        return string.Format("{0},{1},{2},{3}", rect.Left, rect.Top, width, height);
    }
}
'@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$process = $null

function Find-OverlayWindow {
    param(
        [int]$ProcessId,
        [string]$AutomationId,
        [int]$Attempts = 60
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)

    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        Start-Sleep -Milliseconds 250
        $windows = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window))))

        foreach ($window in $windows) {
            try {
                if ($window.Current.ProcessId -ne $ProcessId -or
                    $window.Current.NativeWindowHandle -eq 0) {
                    continue
                }

                $control = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $condition)
                if ($null -ne $control) {
                    return $window
                }
            }
            catch {
                # A window can disappear between FindAll and FindFirst while
                # WinUI is creating the overlay. Retry the next polling pass.
            }
        }
    }

    return $null
}

function Assert-OverlayCapture {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$CapturePath
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    if (-not [ReleaseOverlayVisualNative]::IsWindowVisible($handle)) {
        throw "$Label overlay is not visible."
    }
    if (-not [ReleaseOverlayVisualNative]::HasRoundedWindowRegion($handle)) {
        throw "$Label overlay is not clipped to a rounded native window region."
    }

    $automationBounds = $Window.Current.BoundingRectangle
    # Both overlays use the shared native chrome. Keep this assertion at the
    # chrome contract minimum; the user's saved layout may be any larger size.
    if ($automationBounds.Width -lt 240 -or $automationBounds.Height -lt 56) {
        throw "$Label overlay has invalid bounds: $($automationBounds.Width)x$($automationBounds.Height)."
    }

    $nativeBounds = [ReleaseOverlayVisualNative]::Capture($handle, $CapturePath)
    $bitmap = $null
    try {
        $bitmap = [System.Drawing.Bitmap]::new($CapturePath)
        $sampleRows = [Math]::Min(8, $bitmap.Height)
        $whitePixels = 0
        $sampleCount = 0
        for ($y = 0; $y -lt $sampleRows; $y++) {
            if ($y -eq 0) { continue }
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                # PrintWindow represents the transparent rounded corner
                # envelope with white pixels in the scratch bitmap. Those
                # corners are intentional; the middle of every edge must
                # still be painted by the overlay.
                if (($x -lt 14 -or $x -ge ($bitmap.Width - 14)) -and $y -lt 14) { continue }
                $pixel = $bitmap.GetPixel($x, $y)
                $sampleCount++
                if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) {
                    $whitePixels++
                }
            }
        }

        # Check every edge as well. Text is inset from the edge, so this catches
        # a rounded transparent corner or a native frame without rejecting the
        # intentionally white caption text in the client area.
        $edgeBand = [Math]::Min(6, [Math]::Max(1, [Math]::Min($bitmap.Width, $bitmap.Height) / 4))
        $edgeWhitePixels = 0
        $edgeSampleCount = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($y -eq 0) { continue }
                if ($x -lt $edgeBand -or $x -ge ($bitmap.Width - $edgeBand) -or
                    $y -lt $edgeBand -or $y -ge ($bitmap.Height - $edgeBand)) {
                    if (($x -lt 14 -or $x -ge ($bitmap.Width - 14)) -and
                        ($y -lt 14 -or $y -ge ($bitmap.Height - 14))) { continue }
                    $pixel = $bitmap.GetPixel($x, $y)
                    $edgeSampleCount++
                    if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) {
                        $edgeWhitePixels++
                    }
                }
            }
        }

        $whiteRatio = if ($sampleCount -eq 0) { 1.0 } else { $whitePixels / [double]$sampleCount }
        $edgeWhiteRatio = if ($edgeSampleCount -eq 0) { 1.0 } else { $edgeWhitePixels / [double]$edgeSampleCount }
        if ($whiteRatio -gt 0.05 -or $edgeWhiteRatio -gt 0.005) {
            throw "$Label overlay has white frame pixels: top $whitePixels/$sampleCount, edge $edgeWhitePixels/$edgeSampleCount ($([math]::Round($edgeWhiteRatio * 100, 3))%)."
        }

        Write-Host ("  {0}: {1}; top-strip white pixels {2}/{3}; edge white pixels {4}/{5}; capture {6}" -f
            $Label, $nativeBounds, $whitePixels, $sampleCount, $edgeWhitePixels, $edgeSampleCount, $CapturePath) -ForegroundColor Green
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
}

function Assert-HotkeyToggle {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$Gesture
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $initial = [ReleaseOverlayVisualNative]::IsWindowVisible($handle)
    if (-not $initial) { throw "$Label overlay was not visible before shortcut test." }

    [ReleaseOverlayVisualNative]::TapGesture($Gesture)
    Start-Sleep -Milliseconds 300
    if ([ReleaseOverlayVisualNative]::IsWindowVisible($handle)) {
        throw "$Label overlay did not hide with its $Gesture shortcut."
    }

    [ReleaseOverlayVisualNative]::TapGesture($Gesture)
    Start-Sleep -Milliseconds 300
    if (-not [ReleaseOverlayVisualNative]::IsWindowVisible($handle)) {
        throw "$Label overlay did not show again with its $Gesture shortcut."
    }
    Write-Host "  $Label $Gesture visibility toggle passed." -ForegroundColor Green
}

try {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'VRCTranslate\v2-user-settings.json'
    $configuredQuickInputHotkey = 'Ctrl+Alt+I'
    $configuredVoiceHotkey = 'F7'
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $configuredSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace([string]$configuredSettings.QuickInputHotkey)) {
                $configuredQuickInputHotkey = ([string]$configuredSettings.QuickInputHotkey).Trim()
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$configuredSettings.VoiceHotkey)) {
                $configuredVoiceHotkey = ([string]$configuredSettings.VoiceHotkey).Trim()
            }
        }
        catch {
            Write-Host "  Ignoring unreadable hotkey settings; using defaults." -ForegroundColor Yellow
        }
    }

    $process = Start-Process -FilePath $executable -WorkingDirectory $packageDirectory -PassThru
    $quickInput = Find-OverlayWindow -ProcessId $process.Id -AutomationId 'quick-input-text'
    if ($null -eq $quickInput) {
        throw 'Release package did not create the quick-input overlay.'
    }

    $subtitle = Find-OverlayWindow -ProcessId $process.Id -AutomationId 'subtitle-text'
    if ($null -eq $subtitle) {
        throw 'Release package did not create the subtitle overlay.'
    }

    Assert-OverlayCapture -Window $quickInput -Label 'quick-input' -CapturePath $quickCapturePath
    Assert-OverlayCapture -Window $subtitle -Label 'subtitle' -CapturePath $subtitleCapturePath
    Assert-HotkeyToggle -Window $quickInput -Label 'quick-input' -Gesture $configuredQuickInputHotkey
    Assert-HotkeyToggle -Window $subtitle -Label 'subtitle' -Gesture $configuredVoiceHotkey
    Write-Host 'Release overlay visual smoke passed.' -ForegroundColor Green
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}
