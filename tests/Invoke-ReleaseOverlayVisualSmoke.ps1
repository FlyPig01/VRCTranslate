param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\artifacts\manual-test\VrcTranslate.exe'),
    # Retained for command-line compatibility with earlier visual-smoke runs.
    [string]$OutputDirectory = (Join-Path $env:TEMP 'VRCTranslate-release-overlay-smoke')
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
$packageDirectory = Split-Path -Parent $executable
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
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Release package is missing required asset: $asset"
    }
}

# The overlays now deliberately use normal Windows title bars and resize
# borders. Pixel tests for an absent frame, a custom HRGN or a painted-over
# caption would enforce the retired borderless implementation. The shared
# runtime smoke verifies the replacement contract against the published EXE:
# exact titles, native caption/frame/system-menu styles, layered alpha, UIA
# movement/resizing, shortcuts, persistence and window lifecycle.
$overlaySmoke = Join-Path $PSScriptRoot 'Invoke-V2OverlayWindowSmoke.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $overlaySmoke -Executable $executable
if ($LASTEXITCODE -ne 0) {
    throw "Release native overlay-window smoke failed with exit code $LASTEXITCODE."
}

Write-Host 'Release overlay native-window smoke passed.' -ForegroundColor Green
