param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0\VrcTranslate.exe')
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable -ErrorAction Stop).Path
$packageDirectory = Split-Path -Parent $Executable
$layoutPath = Join-Path $env:LOCALAPPDATA 'VRCTranslate\v2-overlay-layout.json'
$startupLog = Join-Path $env:TEMP 'VrcTranslate-startup.log'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class VrcTranslateOverlayLayoutNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    public static RECT GetRect(IntPtr hwnd)
    {
        RECT rect;
        GetWindowRect(hwnd, out rect);
        return rect;
    }
}
'@

function Find-WindowByProcess([int]$processId, [string]$name)
{
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    return @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)) |
        Where-Object { $_.Current.Name -eq $name -and $_.Current.ProcessId -eq $processId } |
        Select-Object -First 1
}

function Find-Overlay([int]$processId, [string]$automationId)
{
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    $windows = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))) |
        Where-Object { $_.Current.ProcessId -eq $processId }
    foreach ($window in $windows) {
        $child = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $child) { return $window }
    }
    return $null
}

function Wait-Overlay([int]$processId, [string]$automationId)
{
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $window = Find-Overlay $processId $automationId
        if ($null -ne $window -and $window.Current.NativeWindowHandle -ne 0) {
            $handle = [IntPtr]$window.Current.NativeWindowHandle
            if ([VrcTranslateOverlayLayoutNative]::IsWindowVisible($handle)) { return $window }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Could not find visible overlay '$automationId'."
}

function Get-Dimensions($window)
{
    $rect = [VrcTranslateOverlayLayoutNative]::GetRect([IntPtr]$window.Current.NativeWindowHandle)
    return [pscustomobject]@{
        Left = $rect.Left
        Top = $rect.Top
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
    }
}

$backupPath = $null
$firstProcess = $null
$secondProcess = $null
$firstBounds = @{}
try {
    $existing = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue | Where-Object { $_.HandleCount -gt 0 })
    if ($existing.Count -gt 0) { throw 'Close running VrcTranslate.exe processes before layout persistence smoke.' }

    if (Test-Path -LiteralPath $layoutPath) {
        $backupPath = "$layoutPath.layout-smoke-backup-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $layoutPath -Destination $backupPath -Force
    }

    Remove-Item -LiteralPath $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'input'
    $firstProcess = Start-Process -FilePath $Executable -WorkingDirectory $packageDirectory -PassThru
    $shell = $null
    for ($attempt = 0; $attempt -lt 60 -and $null -eq $shell; $attempt++) {
        Start-Sleep -Milliseconds 150
        $shell = Find-WindowByProcess $firstProcess.Id 'VRCTranslate'
    }
    if ($null -eq $shell) { throw 'Layout persistence smoke could not find the shell window.' }

    $input = Wait-Overlay $firstProcess.Id 'quick-input-text'
    $subtitle = Wait-Overlay $firstProcess.Id 'subtitle-text'
    $inputHandle = [IntPtr]$input.Current.NativeWindowHandle
    $subtitleHandle = [IntPtr]$subtitle.Current.NativeWindowHandle
    $inputTarget = @{ X = 120; Y = 140; Width = 900; Height = 180 }
    $subtitleTarget = @{ X = 160; Y = 380; Width = 1000; Height = 180 }
    [VrcTranslateOverlayLayoutNative]::SetWindowPos(
        $inputHandle, [IntPtr]::Zero, $inputTarget.X, $inputTarget.Y,
        $inputTarget.Width, $inputTarget.Height, 0x0004 -bor 0x0010) | Out-Null
    [VrcTranslateOverlayLayoutNative]::SetWindowPos(
        $subtitleHandle, [IntPtr]::Zero, $subtitleTarget.X, $subtitleTarget.Y,
        $subtitleTarget.Width, $subtitleTarget.Height, 0x0004 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 700
    $firstBounds['quick-input'] = Get-Dimensions $input
    $firstBounds['subtitle'] = Get-Dimensions $subtitle

    foreach ($key in @('quick-input', 'subtitle')) {
        $expected = if ($key -eq 'quick-input') { $inputTarget } else { $subtitleTarget }
        $actual = $firstBounds[$key]
        if ([Math]::Abs($actual.Left - $expected.X) -gt 4 -or
            [Math]::Abs($actual.Top - $expected.Y) -gt 4 -or
            [Math]::Abs($actual.Width - $expected.Width) -gt 4 -or
            [Math]::Abs($actual.Height - $expected.Height) -gt 4) {
            throw "$key overlay did not accept the test rectangle: $($actual.Left),$($actual.Top) $($actual.Width)x$($actual.Height)."
        }
    }

    $shell.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 100
        try { $firstProcess.Refresh() } catch { }
        if ($firstProcess.HasExited) { break }
    }
    if (-not $firstProcess.HasExited) { throw 'First layout persistence smoke process did not exit.' }
    if (-not (Test-Path -LiteralPath $layoutPath)) { throw 'Overlay layout file was not written on shutdown.' }

    $secondProcess = Start-Process -FilePath $Executable -WorkingDirectory $packageDirectory -PassThru
    $secondShell = $null
    for ($attempt = 0; $attempt -lt 60 -and $null -eq $secondShell; $attempt++) {
        Start-Sleep -Milliseconds 150
        $secondShell = Find-WindowByProcess $secondProcess.Id 'VRCTranslate'
    }
    if ($null -eq $secondShell) { throw 'Second layout persistence smoke could not find the shell window.' }
    $restoredInput = Wait-Overlay $secondProcess.Id 'quick-input-text'
    $restoredSubtitle = Wait-Overlay $secondProcess.Id 'subtitle-text'
    $restoredBounds = @{
        'quick-input' = Get-Dimensions $restoredInput
        'subtitle' = Get-Dimensions $restoredSubtitle
    }
    foreach ($key in @('quick-input', 'subtitle')) {
        $before = $firstBounds[$key]
        $after = $restoredBounds[$key]
        if ([Math]::Abs($after.Left - $before.Left) -gt 6 -or
            [Math]::Abs($after.Top - $before.Top) -gt 6 -or
            [Math]::Abs($after.Width - $before.Width) -gt 6 -or
            [Math]::Abs($after.Height - $before.Height) -gt 6) {
            throw "$key overlay did not restore its last rectangle: before $($before.Left),$($before.Top) $($before.Width)x$($before.Height); after $($after.Left),$($after.Top) $($after.Width)x$($after.Height)."
        }
    }
    Write-Host '  Overlay position and size persistence passed across process restart.' -ForegroundColor Green
}
finally {
    if ($null -ne $secondProcess -and -not $secondProcess.HasExited) { Stop-Process -Id $secondProcess.Id -Force }
    if ($null -ne $firstProcess -and -not $firstProcess.HasExited) { Stop-Process -Id $firstProcess.Id -Force }
    Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if ($null -ne $backupPath -and (Test-Path -LiteralPath $backupPath)) {
        Move-Item -LiteralPath $backupPath -Destination $layoutPath -Force
    }
    elseif ($null -eq $backupPath) {
        Remove-Item -LiteralPath $layoutPath -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $startupLog) {
    throw "Layout persistence smoke wrote a startup failure log:`n$(Get-Content -LiteralPath $startupLog -Raw)"
}
