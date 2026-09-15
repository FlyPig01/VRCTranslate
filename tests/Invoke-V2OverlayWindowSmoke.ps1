param(
    [Alias('ExecutablePath')]
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0\VrcTranslate.exe'),
    [switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable -ErrorAction Stop).Path
$packageDirectory = Split-Path -Parent $Executable
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path $env:TEMP 'VRCTranslate-overlay-window-smoke'))
$testDataDirectory = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
# The crash log follows VRC_TRANSLATE_DATA_DIR, which this script points at $testDataDirectory.
$startupLog = Join-Path $testDataDirectory 'startup-error.log'
$previousDataDirectory = [Environment]::GetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', 'Process')
$previousSmokePage = [Environment]::GetEnvironmentVariable('VRC_TRANSLATE_SMOKE_PAGE', 'Process')

$existing = @()
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $existing = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue |
        Where-Object { $_.HandleCount -gt 0 })
    if ($existing.Count -eq 0) { break }
    Start-Sleep -Milliseconds 150
}
if ($existing.Count -gt 0) {
    throw 'Close running VrcTranslate.exe processes before the overlay-window smoke test.'
}

New-Item -ItemType Directory -Path $testDataDirectory -Force | Out-Null
$testDataDirectory = (Resolve-Path -LiteralPath $testDataDirectory).Path
[Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $testDataDirectory, 'Process')
[Environment]::SetEnvironmentVariable('VRC_TRANSLATE_SMOKE_PAGE', 'input', 'Process')

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if ($null -eq ('VrcTranslateOverlaySmokeNative' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

public static class VrcTranslateOverlaySmokeNative
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const long WsCaption = 0x00C00000L;
    public const long WsThickFrame = 0x00040000L;
    public const long WsSysMenu = 0x00080000L;
    public const long WsExLayered = 0x00080000L;
    public const uint LwaAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    public static long GetWindowLongValue(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index).ToInt64()
            : GetWindowLong32(hwnd, index);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr hwnd,
        out uint colorKey,
        out byte alpha,
        out uint flags);

    public static byte GetLayeredAlpha(IntPtr hwnd)
    {
        uint colorKey;
        byte alpha;
        uint flags;
        if (!GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLayeredWindowAttributes failed.");
        if ((flags & LwaAlpha) == 0)
            throw new InvalidOperationException("The window does not use a global layered alpha value.");
        return alpha;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static RECT GetRect(IntPtr hwnd)
    {
        RECT rect;
        if (!GetWindowRect(hwnd, out rect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        return rect;
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private const uint KeyUp = 0x0002;

    public static void TapGesture(string gesture)
    {
        if (String.IsNullOrWhiteSpace(gesture))
            throw new ArgumentException("A hotkey gesture is required.", "gesture");

        var modifiers = new List<byte>();
        byte primary = 0;
        foreach (var part in gesture.ToUpperInvariant().Replace(" ", String.Empty).Split('+'))
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
                    if (part.Length == 1)
                    {
                        char value = part[0];
                        if ((value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9'))
                            primary = (byte)value;
                    }
                    else if (part.Length > 1 && part[0] == 'F')
                    {
                        byte number;
                        if (Byte.TryParse(part.Substring(1), out number) && number >= 1 && number <= 12)
                            primary = (byte)(0x6F + number);
                    }
                    break;
            }
        }

        if (primary == 0)
            throw new ArgumentException("Unsupported hotkey gesture: " + gesture, "gesture");

        try
        {
            foreach (var modifier in modifiers)
                KeybdEvent(modifier, 0, 0, UIntPtr.Zero);
            Thread.Sleep(90);
            KeybdEvent(primary, 0, 0, UIntPtr.Zero);
            Thread.Sleep(140);
            KeybdEvent(primary, 0, KeyUp, UIntPtr.Zero);
        }
        finally
        {
            for (var index = modifiers.Count - 1; index >= 0; index--)
                KeybdEvent(modifiers[index], 0, KeyUp, UIntPtr.Zero);
        }
    }
}
'@
}

$automationRoot = [System.Windows.Automation.AutomationElement]::RootElement

function Get-TopLevelWindows {
    param([int]$ProcessId)

    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    return @($automationRoot.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $windowCondition)) | Where-Object {
        try { $_.Current.ProcessId -eq $ProcessId } catch { $false }
    }
}

function Find-MainWindow {
    param([int]$ProcessId)

    return Get-TopLevelWindows $ProcessId | Where-Object {
        try { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.NativeWindowHandle -ne 0 } catch { $false }
    } | Select-Object -First 1
}

function Find-OverlayWindow {
    param(
        [int]$ProcessId,
        [string]$Title,
        [string]$ChildAutomationId
    )

    $childCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $ChildAutomationId)
    foreach ($window in Get-TopLevelWindows $ProcessId) {
        try {
            if ($window.Current.Name -ne $Title -or $window.Current.NativeWindowHandle -eq 0) { continue }
            if ($null -ne $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $childCondition)) {
                return $window
            }
        }
        catch { }
    }
    return $null
}

function Wait-MainWindow {
    param([int]$ProcessId)

    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $window = Find-MainWindow $ProcessId
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 125
    }
    throw 'Could not find the VRCTranslate main window.'
}

function Wait-OverlayWindow {
    param(
        [int]$ProcessId,
        [string]$Title,
        [string]$ChildAutomationId
    )

    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $window = Find-OverlayWindow $ProcessId $Title $ChildAutomationId
        if ($null -ne $window) {
            $handle = [IntPtr]$window.Current.NativeWindowHandle
            if ([VrcTranslateOverlaySmokeNative]::IsWindow($handle) -and
                [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 125
    }
    throw "Could not find the visible '$Title' overlay."
}

function Wait-NativeState {
    param(
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [Parameter(Mandatory)][string]$Failure,
        [int]$Attempts = 60
    )

    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        if (& $Predicate) { return }
        Start-Sleep -Milliseconds 100
    }
    throw $Failure
}

function Find-DescendantById {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-DescendantById {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$AutomationId
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { $element = Find-DescendantById $Window $AutomationId } catch { $element = $null }
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Could not find '$AutomationId' in the main window."
}

function Get-WindowBounds {
    param([IntPtr]$Handle)

    $rect = [VrcTranslateOverlaySmokeNative]::GetRect($Handle)
    return [pscustomobject]@{
        Left = $rect.Left
        Top = $rect.Top
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
    }
}

function Assert-Alpha {
    param(
        [IntPtr]$Handle,
        [double]$Opacity,
        [string]$Label
    )

    $expected = [int][Math]::Round($Opacity * 255d)
    $actual = [int][VrcTranslateOverlaySmokeNative]::GetLayeredAlpha($Handle)
    if ([Math]::Abs($actual - $expected) -gt 1) {
        throw "$Label alpha is $actual; expected $expected for $([Math]::Round($Opacity * 100))% opacity."
    }
}

function Wait-Alpha {
    param(
        [IntPtr]$Handle,
        [double]$Opacity,
        [string]$Label
    )

    $expected = [int][Math]::Round($Opacity * 255d)
    Wait-NativeState -Failure "$Label did not apply $([Math]::Round($Opacity * 100))% opacity in real time." -Predicate {
        try {
            $actual = [int][VrcTranslateOverlaySmokeNative]::GetLayeredAlpha($Handle)
            [Math]::Abs($actual - $expected) -le 1
        }
        catch { $false }
    }
}

function Assert-OverlayContract {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Title,
        [double]$Opacity
    )

    if ($Window.Current.Name -cne $Title) {
        throw "Overlay title is '$($Window.Current.Name)'; expected '$Title'."
    }
    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $style = [VrcTranslateOverlaySmokeNative]::GetWindowLongValue(
        $handle, [VrcTranslateOverlaySmokeNative]::GwlStyle)
    foreach ($required in @(
        @{ Mask = [VrcTranslateOverlaySmokeNative]::WsCaption; Name = 'WS_CAPTION' },
        @{ Mask = [VrcTranslateOverlaySmokeNative]::WsThickFrame; Name = 'WS_THICKFRAME' },
        @{ Mask = [VrcTranslateOverlaySmokeNative]::WsSysMenu; Name = 'WS_SYSMENU' })) {
        if (($style -band [long]$required.Mask) -ne [long]$required.Mask) {
            throw "$Title overlay is missing $($required.Name) (style 0x$($style.ToString('X')))."
        }
    }

    $exStyle = [VrcTranslateOverlaySmokeNative]::GetWindowLongValue(
        $handle, [VrcTranslateOverlaySmokeNative]::GwlExStyle)
    if (($exStyle -band [VrcTranslateOverlaySmokeNative]::WsExLayered) -eq 0) {
        throw "$Title overlay is missing WS_EX_LAYERED."
    }
    Assert-Alpha $handle $Opacity $Title

    try {
        $transform = $Window.GetCurrentPattern(
            [System.Windows.Automation.TransformPattern]::Pattern)
    }
    catch {
        throw "$Title overlay does not expose the UI Automation Transform pattern."
    }
    if (-not $transform.Current.CanMove -or -not $transform.Current.CanResize) {
        throw "$Title overlay is not exposed as a normal movable and resizable Windows window."
    }

    return [pscustomobject]@{
        Handle = $handle
        Transform = $transform
    }
}

function Set-SliderValue {
    param(
        [System.Windows.Automation.AutomationElement]$Slider,
        [double]$Value,
        [string]$Label
    )

    try {
        $range = $Slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    }
    catch {
        throw "$Label does not expose a UI Automation range-value pattern."
    }
    if ($range.Current.IsReadOnly) { throw "$Label is read-only." }
    if ($range.Current.Minimum -gt 60 -or $range.Current.Maximum -lt 100) {
        throw "$Label does not expose the required 60%-100% range."
    }
    $range.SetValue($Value)
}

function Invoke-Navigation {
    param(
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [string]$AutomationId
    )

    $button = Wait-DescendantById $MainWindow $AutomationId
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-ConfiguredHotkey {
    param(
        [string]$Property,
        [string]$Fallback
    )

    $settingsPath = Join-Path $testDataDirectory 'v2-user-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $configured = [string]$settings.$Property
            if (-not [string]::IsNullOrWhiteSpace($configured)) { return $configured.Trim() }
        }
        catch { }
    }
    return $Fallback
}

function Assert-SystemCloseAndHotkeyReuse {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [int]$ProcessId,
        [string]$Title,
        [string]$ChildAutomationId,
        [string]$Hotkey
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-NativeState -Failure "$Title overlay was destroyed, or did not hide, after the system close command." -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindow($handle) -and
        -not [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)
    }

    [VrcTranslateOverlaySmokeNative]::TapGesture($Hotkey)
    Wait-NativeState -Failure "$Title overlay did not reopen with its $Hotkey shortcut." -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindow($handle) -and
        [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)
    }
    $reopened = Wait-OverlayWindow $ProcessId $Title $ChildAutomationId
    $reopenedHandle = [IntPtr]$reopened.Current.NativeWindowHandle
    if ($reopenedHandle -ne $handle) {
        throw "$Title shortcut created HWND $reopenedHandle instead of reusing HWND $handle."
    }
}

function Assert-SubtitleShortcutKeepsForeground {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [string]$Hotkey
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $mainHandle = [IntPtr]$MainWindow.Current.NativeWindowHandle
    $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-NativeState -Failure 'Subtitle overlay did not hide before its no-activation check.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindow($handle) -and
        -not [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)
    }
    [VrcTranslateOverlaySmokeNative]::ShowWindow($mainHandle, 5) | Out-Null
    try { $MainWindow.SetFocus() } catch { }
    [VrcTranslateOverlaySmokeNative]::BringWindowToTop($mainHandle) | Out-Null
    [VrcTranslateOverlaySmokeNative]::SetForegroundWindow($mainHandle) | Out-Null
    Wait-NativeState -Failure 'Could not place the main window in the foreground for the subtitle shortcut check.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::GetForegroundWindow() -eq $mainHandle
    }

    [VrcTranslateOverlaySmokeNative]::TapGesture($Hotkey)
    Wait-NativeState -Failure 'Subtitle shortcut did not show the hidden window.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)
    }
    Start-Sleep -Milliseconds 250
    $foreground = [VrcTranslateOverlaySmokeNative]::GetForegroundWindow()
    if ($foreground -ne $mainHandle) {
        throw "Showing subtitles with $Hotkey stole foreground focus (foreground HWND $foreground instead of $mainHandle)."
    }
}

function Assert-MinimizeAndHotkeyRestore {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Title,
        [string]$Hotkey
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $windowPattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    Wait-NativeState -Failure "$Title overlay did not enter the minimized state." -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsIconic($handle)
    }

    [VrcTranslateOverlaySmokeNative]::TapGesture($Hotkey)
    Wait-NativeState -Failure "$Title overlay did not restore from minimize with one $Hotkey shortcut press." -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle) -and
        -not [VrcTranslateOverlaySmokeNative]::IsIconic($handle)
    }
}

function Assert-SubtitleMinimizeAndHotkeyRestore {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Hotkey
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $windowPattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    Wait-NativeState -Failure 'Subtitle overlay did not enter the minimized state.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsIconic($handle)
    }

    # D3：「他人语音」是整条功能的总开关。关掉它的那次按键同时停止识别并隐藏最小化的
    # 浮窗，再按一次把同一个窗口从最小化恢复出来，而不是新建一个窗口。
    [VrcTranslateOverlaySmokeNative]::TapGesture($Hotkey)
    Wait-NativeState -Failure 'Turning other-player captions off did not take the minimized subtitle overlay off screen.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindow($handle) -and
        -not [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle)
    }
    [VrcTranslateOverlaySmokeNative]::TapGesture($Hotkey)
    Wait-NativeState -Failure 'The subtitle shortcut did not restore the minimized overlay.' -Predicate {
        [VrcTranslateOverlaySmokeNative]::IsWindowVisible($handle) -and
        -not [VrcTranslateOverlaySmokeNative]::IsIconic($handle)
    }
}

function Resize-And-MoveOverlay {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [object]$Contract,
        [string]$Label,
        [int]$VerticalSlot,
        [switch]$CheckInputGrowth
    )

    $handle = [IntPtr]$Contract.Handle
    $workArea = [System.Windows.Forms.Screen]::FromHandle($handle).WorkingArea
    $smallWidth = [Math]::Max(420, [Math]::Min(760, $workArea.Width - 260))
    $largeWidth = [Math]::Min($workArea.Width - 80, $smallWidth + 140)
    $smallHeight = [Math]::Max(140, [Math]::Min(180, $workArea.Height - 280))
    $largeHeight = [Math]::Min($workArea.Height - 80, $smallHeight + 90)
    if ($largeWidth -le ($smallWidth + 40) -or $largeHeight -le ($smallHeight + 30)) {
        throw "$Label smoke requires a work area of at least 640x360 pixels."
    }

    $targetX = $workArea.Left + 36 + ($VerticalSlot * 24)
    $targetY = $workArea.Top + 48 + ($VerticalSlot * [Math]::Min(260, [Math]::Max(120, $workArea.Height / 3)))
    $targetY = [Math]::Min($targetY, $workArea.Bottom - $largeHeight - 40)

    $Contract.Transform.Move([double]$targetX, [double]$targetY)
    $Contract.Transform.Resize([double]$smallWidth, [double]$smallHeight)
    Start-Sleep -Milliseconds 350
    $smallBounds = Get-WindowBounds $handle
    if ([Math]::Abs($smallBounds.Left - $targetX) -gt 10 -or
        [Math]::Abs($smallBounds.Top - $targetY) -gt 10 -or
        [Math]::Abs($smallBounds.Width - $smallWidth) -gt 12 -or
        [Math]::Abs($smallBounds.Height - $smallHeight) -gt 12) {
        throw "$Label did not accept normal UIA move/resize: $($smallBounds.Left),$($smallBounds.Top) $($smallBounds.Width)x$($smallBounds.Height)."
    }

    if ($CheckInputGrowth) {
        $input = Find-DescendantById $Window 'quick-input-text'
        if ($null -eq $input) { throw 'Input overlay has no quick-input-text editor.' }
        $editorBefore = $input.Current.BoundingRectangle
    }

    $Contract.Transform.Resize([double]$largeWidth, [double]$largeHeight)
    Start-Sleep -Milliseconds 450
    $largeBounds = Get-WindowBounds $handle
    if ($largeBounds.Width -lt ($smallBounds.Width + 80) -or
        $largeBounds.Height -lt ($smallBounds.Height + 45)) {
        throw "$Label did not resize through the normal Windows transform provider."
    }

    if ($CheckInputGrowth) {
        $editorAfter = $input.Current.BoundingRectangle
        # 输入行是贴顶的固定高度条带：窗口变高时它只跟着变宽，高度和贴顶间距不动，
        # 多出来的高度必须全部落在线的下方结果区。旧断言要求编辑框跟着窗口长高，
        # 那正是"窗口一高文字就飘到中间"的成因，这里按新结构重新表达同一个意图。
        if ($editorAfter.Width -lt ($editorBefore.Width + 70)) {
            throw "Input editor did not follow its window width: before $([Math]::Round($editorBefore.Width)); after $([Math]::Round($editorAfter.Width))."
        }
        # UIA 矩形是物理像素（本机 150% 缩放时 46 DIP 读作 69px），所以这里只比较
        # 前后变化和它相对窗口的占比，不写死任何像素高度。
        if ([Math]::Abs($editorAfter.Height - $editorBefore.Height) -gt 6) {
            throw "Input editor must keep its fixed top strip: before $([Math]::Round($editorBefore.Height))px; after $([Math]::Round($editorAfter.Height))px."
        }
        if ($editorAfter.Height -ge ($largeBounds.Height * 0.5)) {
            throw "Input editor must stay a compact top strip instead of filling the window: $([Math]::Round($editorAfter.Height))px of $($largeBounds.Height)px."
        }
        $editorInsetBefore = $editorBefore.Top - $smallBounds.Top
        $editorInsetAfter = $editorAfter.Top - $largeBounds.Top
        if ([Math]::Abs($editorInsetAfter - $editorInsetBefore) -gt 6) {
            throw "Input editor must stay pinned to the top edge: inset before $([Math]::Round($editorInsetBefore))px; after $([Math]::Round($editorInsetAfter))px."
        }
        $resultSpaceBefore = ($smallBounds.Top + $smallBounds.Height) - $editorBefore.Bottom
        $resultSpaceAfter = ($largeBounds.Top + $largeBounds.Height) - $editorAfter.Bottom
        if ($resultSpaceAfter -lt ($resultSpaceBefore + 35)) {
            throw "The result area below the editor must absorb the window growth: before $([Math]::Round($resultSpaceBefore))px; after $([Math]::Round($resultSpaceAfter))px."
        }
    }

    return $largeBounds
}

function Close-MainAndAssertOverlayDestruction {
    param(
        [System.Diagnostics.Process]$Process,
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [IntPtr[]]$OverlayHandles
    )

    $MainWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-NativeState -Attempts 80 -Failure 'Closing the main window did not destroy both overlay HWNDs.' -Predicate {
        -not ($OverlayHandles | Where-Object { [VrcTranslateOverlaySmokeNative]::IsWindow($_) })
    }
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 100
        try { $Process.Refresh() } catch { }
        if ($Process.HasExited) { return }
    }
    throw 'Closing the main window left VrcTranslate.exe running.'
}

function Assert-BoundsRestored {
    param(
        [object]$Expected,
        [object]$Actual,
        [string]$Label
    )

    if ([Math]::Abs($Actual.Left - $Expected.Left) -gt 10 -or
        [Math]::Abs($Actual.Top - $Expected.Top) -gt 10 -or
        [Math]::Abs($Actual.Width - $Expected.Width) -gt 12 -or
        [Math]::Abs($Actual.Height - $Expected.Height) -gt 12) {
        throw "$Label did not restore its rectangle: expected $($Expected.Left),$($Expected.Top) $($Expected.Width)x$($Expected.Height); received $($Actual.Left),$($Actual.Top) $($Actual.Width)x$($Actual.Height)."
    }
}

$firstProcess = $null
$secondProcess = $null
$testPassed = $false
try {
    Remove-Item -LiteralPath $startupLog -Force -ErrorAction SilentlyContinue
    Write-Host '[overlay 1/4] Starting isolated native-window smoke'
    $firstProcess = Start-Process -FilePath $Executable -WorkingDirectory $packageDirectory -PassThru
    $main = Wait-MainWindow $firstProcess.Id
    $input = Wait-OverlayWindow $firstProcess.Id '输入' 'quick-input-text'
    # D3：字幕浮窗没有独立入口，它随他人语音识别一起出现；一次总开关把两者带上来。
    $subtitleHotkey = Get-ConfiguredHotkey 'VoiceHotkey' 'F7'
    [VrcTranslateOverlaySmokeNative]::TapGesture($subtitleHotkey)
    $subtitle = Wait-OverlayWindow $firstProcess.Id '字幕' 'subtitle-text'

    $inputContract = Assert-OverlayContract $input '输入' 0.90
    $subtitleContract = Assert-OverlayContract $subtitle '字幕' 0.90
    Write-Host '  Native titles, caption/frame/system-menu styles, layered 90% alpha and Transform patterns passed.' -ForegroundColor Green

    Write-Host '[overlay 2/4] Exercising independent live opacity controls'
    $inputSlider = Wait-DescendantById $main 'input-overlay-opacity'
    Set-SliderValue $inputSlider 70 'Input opacity slider'
    Wait-Alpha $inputContract.Handle 0.70 'Input overlay'
    Assert-Alpha $subtitleContract.Handle 0.90 'Subtitle overlay after input change'

    Invoke-Navigation $main 'nav-voice'
    $subtitleSlider = Wait-DescendantById $main 'subtitle-overlay-opacity'
    Set-SliderValue $subtitleSlider 80 'Subtitle opacity slider'
    Wait-Alpha $subtitleContract.Handle 0.80 'Subtitle overlay'
    Assert-Alpha $inputContract.Handle 0.70 'Input overlay after subtitle change'
    Write-Host '  Input and subtitle sliders changed only their own HWND alpha in real time.' -ForegroundColor Green

    Write-Host '[overlay 3/4] Exercising native move, resize, close and shortcut behavior'
    $inputBounds = Resize-And-MoveOverlay $input $inputContract 'Input overlay' 0 -CheckInputGrowth
    $subtitleBounds = Resize-And-MoveOverlay $subtitle $subtitleContract 'Subtitle overlay' 1

    $inputHotkey = Get-ConfiguredHotkey 'QuickInputHotkey' 'Ctrl+Alt+I'
    Assert-SystemCloseAndHotkeyReuse $input $firstProcess.Id '输入' 'quick-input-text' $inputHotkey
    Assert-SystemCloseAndHotkeyReuse $subtitle $firstProcess.Id '字幕' 'subtitle-text' $subtitleHotkey
    Assert-SubtitleShortcutKeepsForeground $subtitle $main $subtitleHotkey
    Assert-MinimizeAndHotkeyRestore $input '输入' $inputHotkey
    Assert-SubtitleMinimizeAndHotkeyRestore $subtitle $subtitleHotkey
    Write-Host '  Close hides, shortcuts reuse HWNDs, subtitles keep focus, and one shortcut restores a minimized overlay.' -ForegroundColor Green

    $firstInputHandle = [IntPtr]$inputContract.Handle
    $firstSubtitleHandle = [IntPtr]$subtitleContract.Handle
    Close-MainAndAssertOverlayDestruction $firstProcess $main @($firstInputHandle, $firstSubtitleHandle)
    if (Test-Path -LiteralPath $startupLog) {
        throw "First overlay smoke launch wrote a startup failure log:`n$(Get-Content -LiteralPath $startupLog -Raw)"
    }

    $appearancePath = Join-Path $testDataDirectory 'v2-overlay-appearance.json'
    $layoutPath = Join-Path $testDataDirectory 'v2-overlay-layout.json'
    if (-not (Test-Path -LiteralPath $appearancePath) -or -not (Test-Path -LiteralPath $layoutPath)) {
        throw 'Overlay appearance or layout was not persisted in VRC_TRANSLATE_DATA_DIR.'
    }

    Write-Host '[overlay 4/4] Restarting and verifying persisted opacity and rectangles'
    Remove-Item -LiteralPath $startupLog -Force -ErrorAction SilentlyContinue
    $secondProcess = Start-Process -FilePath $Executable -WorkingDirectory $packageDirectory -PassThru
    $secondMain = Wait-MainWindow $secondProcess.Id
    $restoredInput = Wait-OverlayWindow $secondProcess.Id '输入' 'quick-input-text'
    # 重启后字幕浮窗同样是关着的，直到总开关把它和识别一起打开。
    [VrcTranslateOverlaySmokeNative]::TapGesture($subtitleHotkey)
    $restoredSubtitle = Wait-OverlayWindow $secondProcess.Id '字幕' 'subtitle-text'
    $restoredInputContract = Assert-OverlayContract $restoredInput '输入' 0.70
    $restoredSubtitleContract = Assert-OverlayContract $restoredSubtitle '字幕' 0.80
    Assert-BoundsRestored $inputBounds (Get-WindowBounds $restoredInputContract.Handle) 'Input overlay'
    Assert-BoundsRestored $subtitleBounds (Get-WindowBounds $restoredSubtitleContract.Handle) 'Subtitle overlay'

    Close-MainAndAssertOverlayDestruction $secondProcess $secondMain @(
        [IntPtr]$restoredInputContract.Handle,
        [IntPtr]$restoredSubtitleContract.Handle)
    if (Test-Path -LiteralPath $startupLog) {
        throw "Second overlay smoke launch wrote a startup failure log:`n$(Get-Content -LiteralPath $startupLog -Raw)"
    }

    $testPassed = $true
    Write-Host 'V2 native overlay-window smoke passed.' -ForegroundColor Green
}
finally {
    foreach ($candidate in @($secondProcess, $firstProcess)) {
        if ($null -ne $candidate) {
            try { $candidate.Refresh() } catch { }
            if (-not $candidate.HasExited) {
                Stop-Process -Id $candidate.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Start-Sleep -Milliseconds 400
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:VRC_TRANSLATE_DATA_DIR -ErrorAction SilentlyContinue
    }
    else {
        [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $previousDataDirectory, 'Process')
    }
    if ($null -eq $previousSmokePage) {
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
    }
    else {
        [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_SMOKE_PAGE', $previousSmokePage, 'Process')
    }

    if (-not $KeepTestData) {
        $resolvedData = [System.IO.Path]::GetFullPath($testDataDirectory)
        if ($resolvedData.StartsWith($temporaryRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedData -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    elseif ($testPassed) {
        Write-Host "  Isolated test data retained at: $testDataDirectory"
    }
}
