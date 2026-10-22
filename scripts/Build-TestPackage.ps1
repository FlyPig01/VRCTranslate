param(
    [string]$SourceData = '',
    [switch]$SkipBuild,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$distributionScript = Join-Path $repoRoot 'scripts\Build-Distribution.ps1'
$distRoot = Join-Path $repoRoot 'dist'

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]($props.Project.PropertyGroup.VersionPrefix | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VersionPrefix must be a stable three-part version, got '$version'."
}

$formalDirectory = Join-Path $distRoot "VRCTranslate-v$version-win-x64"
$testDirectory = Join-Path $distRoot "VRCTranslate-v$version-test"
$testZip = Join-Path $distRoot "VRCTranslate-v$version-test.zip"

function Assert-TestPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [System.IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch a path outside dist: $full"
    }
}

function Find-ConfiguredData {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($SourceData)) {
        $candidates += [System.IO.Path]::GetFullPath($SourceData)
    }

    $candidates += Get-ChildItem -LiteralPath $distRoot -Directory -Filter 'VRCTranslate-v*-test' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Join-Path $_.FullName 'data' }

    $artifactDataRoot = Join-Path $repoRoot 'artifacts\test-data'
    $candidates += $artifactDataRoot
    if (Test-Path -LiteralPath $artifactDataRoot -PathType Container) {
        $candidates += Get-ChildItem -LiteralPath $artifactDataRoot -Directory |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { $_.FullName }
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
        if (Test-Path -LiteralPath (Join-Path $candidate 'v2-qwen-asr.json')) { return $candidate }
        if (Test-Path -LiteralPath (Join-Path $candidate 'v2-tencent-asr.json')) { return $candidate }
        if (Test-Path -LiteralPath (Join-Path $candidate 'v2-profiles.json')) { return $candidate }
    }

    return $null
}

$dataSource = Find-ConfiguredData
if ($null -eq $dataSource) {
    throw 'No existing data directory found. Pass -SourceData with a configured data folder.'
}

# Build-Distribution may replace the formal folder that held the live test
# configuration, so snapshot the data before rebuilding.
$dataBackup = Join-Path $env:TEMP ("vrctranslate-test-data-" + [Guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $dataSource -Destination $dataBackup -Recurse -Force
$dataSource = $dataBackup

try {
    if (-not $SkipBuild -or -not (Test-Path -LiteralPath $formalDirectory -PathType Container)) {
        & $distributionScript
        if ($LASTEXITCODE -ne 0) { throw "Build-Distribution failed with exit code $LASTEXITCODE." }
    }

    Assert-TestPath -Path $testDirectory
    Assert-TestPath -Path $testZip
    if (Test-Path -LiteralPath $testDirectory) {
        Remove-Item -LiteralPath $testDirectory -Recurse -Force
    }
    Copy-Item -LiteralPath $formalDirectory -Destination $testDirectory -Recurse -Force
    $testData = Join-Path $testDirectory 'data'
    if (Test-Path -LiteralPath $testData) {
        Remove-Item -LiteralPath $testData -Recurse -Force
    }
    Copy-Item -LiteralPath $dataSource -Destination $testData -Recurse -Force
}
finally {
    if (Test-Path -LiteralPath $dataBackup) {
        Remove-Item -LiteralPath $dataBackup -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$qwenPath = Join-Path $testDirectory 'data\v2-qwen-asr.json'
if (Test-Path -LiteralPath $qwenPath) {
    $qwen = Get-Content -LiteralPath $qwenPath -Raw | ConvertFrom-Json
    Write-Host ("Kept Qwen settings: enabled={0}, workspace={1}, key={2}" -f
        $qwen.Enabled, $qwen.WorkspaceId,
        $(if ([string]::IsNullOrWhiteSpace($qwen.ApiKeyProtected)) { 'missing' } else { 'present' }))
}
$tencentPath = Join-Path $testDirectory 'data\v2-tencent-asr.json'
if (Test-Path -LiteralPath $tencentPath) {
    $tencent = Get-Content -LiteralPath $tencentPath -Raw | ConvertFrom-Json
    Write-Host ("Kept Tencent settings: enabled={0}, appid={1}, secret={2}" -f
        $tencent.Enabled, $tencent.AppId,
        $(if ([string]::IsNullOrWhiteSpace($tencent.SecretKeyProtected)) { 'missing' } else { 'present' }))
}

if (-not $NoZip) {
    if (Test-Path -LiteralPath $testZip) { Remove-Item -LiteralPath $testZip -Force }
    Compress-Archive -Path (Join-Path $testDirectory '*') -DestinationPath $testZip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $testZip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Test ZIP: $testZip"
    Write-Host "SHA-256: $hash"
}

Write-Host "Test package: $testDirectory"
Write-Host 'This folder contains your local data and DPAPI-protected credentials; do not share it.'
