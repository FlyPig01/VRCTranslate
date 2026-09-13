param(
    [ValidateSet('run', 'input', 'voice', 'translation', 'settings', 'guide')]
    [string]$Page = 'run',
    [ValidateSet('main', 'subtitle', 'quick-input')]
    [string]$Target = 'main',
    [string]$Executable = '',
    [string]$OutputPath = (Join-Path $env:TEMP 'v2-runtime-window.png'),
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$drawingAssembly = [System.Drawing.Bitmap].Assembly.Location
$gdiPlusAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.GdiPlus.dll'
$windowsCoreAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.Core.dll'
$drawingReferences = @($drawingAssembly)
if (Test-Path $gdiPlusAssembly) { $drawingReferences += $gdiPlusAssembly }
if (Test-Path $windowsCoreAssembly) { $drawingReferences += $windowsCoreAssembly }
Add-Type -ReferencedAssemblies $drawingReferences @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ActualWindowCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    public static string Capture(IntPtr hwnd, string path) {
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("GetWindowRect failed");
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            IntPtr hdc = graphics.GetHdc();
            try { if (!PrintWindow(hwnd, hdc, 2)) throw new InvalidOperationException("PrintWindow failed"); }
            finally { graphics.ReleaseHdc(hdc); }
            bitmap.Save(path, ImageFormat.Png);
        }
        return string.Format("{0},{1},{2},{3}", rect.Left, rect.Top, width, height);
    }
}
'@

if ($Width -gt 0 -and $Height -gt 0) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ActualWindowResize {
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
'@
}

$defaultOutput = (Resolve-Path (Join-Path $PSScriptRoot '..\src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0')).Path
$exe = if ([string]::IsNullOrWhiteSpace($Executable)) {
    Join-Path $defaultOutput 'VrcTranslate.exe'
} else {
    (Resolve-Path -LiteralPath $Executable).Path
}
$output = Split-Path -Parent $exe
$startupLog = Join-Path $output 'data\startup-error.log'
Remove-Item $startupLog -ErrorAction SilentlyContinue
$env:VRC_TRANSLATE_SMOKE_PAGE = $Page
$process = Start-Process -FilePath $exe -WorkingDirectory $output -PassThru
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $window = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 250
        $window = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)) |
            Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
            Select-Object -First 1
    }
    if ($null -eq $window) { throw 'VRCTranslate window was not found.' }
    if ($Target -ne 'main') {
        $targetName = if ($Target -eq 'subtitle') { '打开语音' } else { '打开输入框' }
        $openCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $targetName)
        $openButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $openCondition)
        if ($null -eq $openButton) { throw "The main page does not expose '$targetName'." }
        $openButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $child = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $child; $attempt++) {
            Start-Sleep -Milliseconds 200
            $childWindows = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window))))
        $targetAutomationId = if ($Target -eq 'subtitle') { 'subtitle-text' } else { 'quick-input-text' }
        $child = $childWindows |
            Where-Object {
                $_.Current.ProcessId -eq $process.Id -and
                $_.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle -and
                $null -ne $_.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                        $targetAutomationId)))
            } |
                Select-Object -First 1
        }
        if ($null -eq $child) { throw "Opening '$Target' did not create the expected window." }
        $window = $child
    }
    # WinUI creates the shell before the navigated page has completed XAML
    # measure and its async state restore. Wait for the first frame to settle
    # so screenshots represent the page users actually see.
    Start-Sleep -Seconds 2
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    if ($Width -gt 0 -and $Height -gt 0) {
        [ActualWindowResize]::SetWindowPos($handle, [IntPtr]::Zero, 80, 80, $Width, $Height, 0x0040) | Out-Null
        Start-Sleep -Milliseconds 700
    }
    $screenshot = $OutputPath
    $nativeBounds = [ActualWindowCapture]::Capture($handle, $screenshot)
    $bounds = $window.Current.BoundingRectangle
    [pscustomobject]@{
        ProcessId = $process.Id
        WindowName = $window.Current.Name
        Handle = ('0x{0:X}' -f $handle.ToInt64())
        NativeBounds = $nativeBounds
        AutomationBounds = ('{0},{1},{2},{3}' -f $bounds.Left, $bounds.Top, $bounds.Width, $bounds.Height)
        Screenshot = $screenshot
        StartupLogExists = (Test-Path $startupLog)
    } | Format-List
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
}
