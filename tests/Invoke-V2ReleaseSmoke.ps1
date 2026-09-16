param(
    [string] $Executable = (Join-Path $PSScriptRoot '..\artifacts\manual-test\VrcTranslate.exe'),
    [string] $ExpectedVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$OutputDirectory = Split-Path -Parent $Executable
$SmokeDataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
    'VRCTranslate-release-smoke-' + [Guid]::NewGuid().ToString('N'))
$StartupLog = Join-Path $SmokeDataDirectory 'startup-error.log'
$PreviousDataDirectory = [Environment]::GetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', 'Process')
$IconPath = Join-Path $OutputDirectory 'app.ico'

if (-not (Test-Path -LiteralPath $IconPath)) { throw "Release icon is missing: $IconPath" }
if ((Get-Item -LiteralPath $IconPath).Length -lt 4000) { throw 'Release icon is unexpectedly small.' }
if ((Get-Item -LiteralPath $Executable).Length -lt 100000) { throw 'Release executable is unexpectedly small.' }
$VersionInfo = (Get-Item -LiteralPath $Executable).VersionInfo
if ($VersionInfo.FileVersion -ne "$ExpectedVersion.0") {
    throw "Release FileVersion is '$($VersionInfo.FileVersion)'; expected '$ExpectedVersion.0'."
}
if ($VersionInfo.ProductVersion -ne $ExpectedVersion) {
    throw "Release ProductVersion is '$($VersionInfo.ProductVersion)'; expected '$ExpectedVersion'."
}

$existing = @()
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $existing = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue |
        Where-Object { $_.HandleCount -gt 0 })
    if ($existing.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
}
if ($existing.Count -gt 0) {
    throw 'Close running VrcTranslate.exe processes before the Release smoke test.'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-DescendantByName($root, [string] $name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-DescendantById($root, [string] $automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

New-Item -ItemType Directory -Path $SmokeDataDirectory -Force | Out-Null
[Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $SmokeDataDirectory, 'Process')
$process = $null
$startupFailure = $null
try {
    $process = Start-Process -FilePath $Executable -WorkingDirectory $OutputDirectory -PassThru
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $window = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 200
        $window = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)) |
            Where-Object {
                $_.Current.Name -eq 'VRCTranslate' -and
                $_.Current.ProcessId -eq $process.Id
            } |
            Select-Object -First 1
    }
    if ($null -eq $window) { throw 'Release smoke could not find the VRCTranslate window.' }

    $brand = Find-DescendantByName $window 'VRCTranslate 图标'
    if ($null -eq $brand -or $brand.Current.BoundingRectangle.Width -lt 16) {
        throw 'Release smoke could not find the visible sidebar icon.'
    }

    $configure = Find-DescendantById $window 'route-summary-config'
    if ($null -eq $configure) { throw 'Release smoke could not find the translation service entry.' }
    $configure.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $profileAdd = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $profileAdd; $attempt++) {
        Start-Sleep -Milliseconds 150
        $profileAdd = Find-DescendantById $window 'translation-profile-add'
    }
    if ($null -eq $profileAdd) { throw 'Release smoke could not open the translation profile page.' }

    $navTranslation = Find-DescendantById $window 'nav-translation'
    if ($null -eq $navTranslation) { throw 'Release smoke could not find translation navigation.' }
    $navTranslation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500

    $edit = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $edit; $attempt++) {
        Start-Sleep -Milliseconds 150
        $edit = @($window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) |
            Where-Object {
                $_.Current.AutomationId -match '^edit-.+' -and
                $_.Current.BoundingRectangle.Width -gt 0
            } |
            Select-Object -First 1
    }
    if ($null -eq $edit) { throw 'Release smoke could not find a translation profile edit action.' }
    $edit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $dialog = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $dialog; $attempt++) {
        Start-Sleep -Milliseconds 150
        $dialog = Find-DescendantByName $window '编辑翻译服务档案'
    }
    if ($null -eq $dialog) { throw 'Release smoke could not open the profile editor dialog.' }
    $cancel = Find-DescendantByName $dialog '取消'
    if ($null -eq $cancel) { throw 'Release profile editor has no cancel action.' }
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250

    $process.Refresh()
    if ($process.HasExited) { throw "Release process exited unexpectedly with code $($process.ExitCode)." }

    # Closing the shell must close the two independent overlay HWNDs as well.
    # Otherwise the overlays keep the Win32 process alive after the user exits
    # the main window and leave an invisible background process behind.
    try {
        $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }
    catch {
        throw "Release smoke could not close the main VRCTranslate window: $($_.Exception.Message)"
    }
    $exited = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 100
        try { $process.Refresh() } catch { }
        if ($process.HasExited) {
            $exited = $true
            break
        }
    }
    if (-not $exited) {
        throw 'Closing the main window left VrcTranslate.exe running; overlay shutdown did not complete.'
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $StartupLog) {
        $startupFailure = Get-Content -LiteralPath $StartupLog -Raw
    }
    [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $PreviousDataDirectory, 'Process')
    Remove-Item -LiteralPath $SmokeDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

if ($null -ne $startupFailure) {
    throw "Release smoke wrote a startup failure log:`n$startupFailure"
}

# The functional smoke above validates navigation and the profile dialog. Run
# the native overlay pass as part of the same release gate so the published
# EXE must retain standard title bars, movement/resizing, layered opacity,
# shortcut reuse, persistence and shell-linked shutdown.
$visualSmoke = Join-Path $PSScriptRoot 'Invoke-ReleaseOverlayVisualSmoke.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $visualSmoke -ExecutablePath $Executable
if ($LASTEXITCODE -ne 0) {
    throw "Release overlay visual smoke failed with exit code $LASTEXITCODE."
}
Write-Host 'Release smoke passed, including main-window overlay shutdown.' -ForegroundColor Green
