param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$desktopProject = Join-Path $repoRoot 'src\VrcTranslate.Desktop\VrcTranslate.Desktop.csproj'
$launcherProject = Join-Path $repoRoot 'src\VrcTranslate.Launcher\VrcTranslate.Launcher.csproj'
$validationScript = Join-Path $repoRoot 'tests\Invoke-V2Validation.ps1'
$releaseSmokeScript = Join-Path $repoRoot 'tests\Invoke-V2ReleaseSmoke.ps1'
$launcherSmokeScript = Join-Path $repoRoot 'tests\Invoke-DistributionLauncherSmoke.ps1'
$distRoot = Join-Path $repoRoot 'dist'
$launcherPublishRoot = Join-Path $repoRoot 'artifacts\release-launcher'
$zipCheckRoot = Join-Path $repoRoot 'artifacts\release-zip-check'

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]($props.Project.PropertyGroup.VersionPrefix | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "正式发布需要稳定的三段版本号，当前为 '$version'。"
}

$packageName = "VRCTranslate-v$version-win-x64"
$packageDirectory = Join-Path $distRoot $packageName
$programDirectory = Join-Path $packageDirectory '程序文件'
$noticeDirectory = Join-Path $packageDirectory '说明与许可'
$launcherPath = Join-Path $packageDirectory 'VRCTranslate.exe'
$internalExecutable = Join-Path $programDirectory 'VrcTranslate.exe'
$zipPath = Join-Path $distRoot "$packageName.zip"
$checksumPath = Join-Path $distRoot 'SHA256SUMS.txt'

function Assert-SafeWorkspacePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )

    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expectedFull = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\')
    if (-not $full.Equals($expectedFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理非预期目录：$full"
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝清理重解析目录：$full"
        }
    }
}

function Add-SecretCandidates {
    param(
        [AllowNull()][object]$Node,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]]$Secrets,
        [string]$PropertyName = ''
    )

    if ($null -eq $Node) { return }
    if ($Node -is [string]) {
        if ($PropertyName -match '(?i)(credential|secret|api.?key|access.?key|token|password)' -and
            $Node.Length -ge 12 -and
            $Node -ne '本地配置' -and
            $Node -notmatch '^https?://') {
            [void]$Secrets.Add($Node)
        }
        return
    }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) {
            Add-SecretCandidates -Node $property.Value -Secrets $Secrets -PropertyName $property.Name
        }
        return
    }
    if ($Node -is [System.Collections.IEnumerable]) {
        foreach ($item in $Node) {
            Add-SecretCandidates -Node $item -Secrets $Secrets -PropertyName $PropertyName
        }
    }
}

function Get-KnownLocalSecrets {
    $secrets = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sources = @(
        (Join-Path $env:LOCALAPPDATA 'VRCTranslate\v2-profiles.json'),
        (Join-Path $repoRoot 'artifacts\manual-test\data\v2-profiles.json'),
        (Join-Path $repoRoot 'artifacts\profile-backup\v2-profiles.json'))
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        try {
            $document = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
            Add-SecretCandidates -Node $document -Secrets $secrets
        }
        catch {
            throw "无法检查本机凭据来源文件：$source"
        }
    }
    return ,$secrets
}

$knownLocalSecrets = Get-KnownLocalSecrets

function Assert-NoKnownLocalSecrets {
    param([Parameter(Mandatory)][string]$Root)

    if ($knownLocalSecrets.Count -eq 0) { return }
    $textFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {
        $_.Extension -in @('.json', '.xml', '.config', '.txt', '.md') -and $_.Length -le 10MB
    })
    foreach ($file in $textFiles) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($secret in $knownLocalSecrets) {
            if ($content.Contains($secret, [StringComparison]::Ordinal)) {
                $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
                throw "发布包包含本机凭据内容：$relative"
            }
        }
    }
}

function Assert-CleanPackage {
    param([Parameter(Mandatory)][string]$Root)

    $allowedRootEntries = @('VRCTranslate.exe', '使用说明.txt', '程序文件', '说明与许可')
    $unexpectedRootEntries = @(Get-ChildItem -LiteralPath $Root -Force | Where-Object {
        $_.Name -notin $allowedRootEntries
    })
    if ($unexpectedRootEntries.Count -gt 0) {
        throw "发布包根目录存在多余项目：$($unexpectedRootEntries[0].Name)"
    }

    foreach ($required in @(
        'VRCTranslate.exe',
        '使用说明.txt',
        '说明与许可\LICENSE',
        '说明与许可\DISCLAIMER.md',
        '说明与许可\THIRD_PARTY_NOTICES.md',
        '程序文件\VrcTranslate.exe',
        '程序文件\app.ico',
        '程序文件\App.xbf',
        '程序文件\MainWindow.xbf',
        '程序文件\VrcTranslate.pri',
        '程序文件\Resources\default-glossary.json',
        '程序文件\Models\sensevoice\model.int8.onnx',
        '程序文件\Models\sensevoice\tokens.txt',
        '程序文件\Models\speaker\segmentation.int8.onnx',
        '程序文件\Models\speaker\embedding.onnx',
        '程序文件\Models\vad\silero_vad.onnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $required) -PathType Leaf)) {
            throw "发布包缺少：$required"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $Root 'data')) {
        throw '正式发布包不得包含 data 目录。'
    }
    if (Test-Path -LiteralPath (Join-Path $Root '程序文件\data')) {
        throw '程序文件目录不得包含 data 目录。'
    }

    $forbidden = @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Where-Object {
        -not $_.PSIsContainer -and (
            $_.Extension -in @('.pdb', '.log') -or
            $_.Name -in @('v2-profiles.json', 'v2-route.json', 'v2-user-settings.json'))
    })
    if ($forbidden.Count -gt 0) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $forbidden[0].FullName)
        throw "发布包包含禁止文件：$relative"
    }

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $Root '程序文件\VrcTranslate.runtimeconfig.json') -Raw |
        ConvertFrom-Json
    if ($null -eq $runtimeConfig.runtimeOptions.includedFrameworks) {
        throw '发布包不是自包含运行时。'
    }

    foreach ($executablePath in @(
        (Join-Path $Root 'VRCTranslate.exe'),
        (Join-Path $Root '程序文件\VrcTranslate.exe'))) {
        $versionInfo = (Get-Item -LiteralPath $executablePath).VersionInfo
        if ($versionInfo.FileVersion -ne "$version.0" -or $versionInfo.ProductVersion -ne $version) {
            throw "EXE 版本错误：FileVersion=$($versionInfo.FileVersion)，ProductVersion=$($versionInfo.ProductVersion)。"
        }
    }

    Assert-NoKnownLocalSecrets -Root $Root
}

Write-Host '[1/9] 运行全量自动验证'
& pwsh -NoProfile -ExecutionPolicy Bypass -File $validationScript
if ($LASTEXITCODE -ne 0) { throw "全量验证失败，退出码 $LASTEXITCODE。" }

Assert-SafeWorkspacePath -Path $distRoot -Expected (Join-Path $repoRoot 'dist')
if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $programDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null

Write-Host '[2/9] 发布主程序到“程序文件”目录'
& dotnet publish $desktopProject `
    -c Release `
    -p:Platform=x64 `
    -p:Version=$version `
    -r win-x64 `
    --self-contained true `
    -o $programDirectory
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败，退出码 $LASTEXITCODE。" }

Write-Host '[3/9] 生成根目录单文件启动器'
Assert-SafeWorkspacePath -Path $launcherPublishRoot -Expected (Join-Path $repoRoot 'artifacts\release-launcher')
if (Test-Path -LiteralPath $launcherPublishRoot) {
    Remove-Item -LiteralPath $launcherPublishRoot -Recurse -Force
}
& dotnet publish $launcherProject `
    -c Release `
    -p:Version=$version `
    -r win-x64 `
    --self-contained true `
    -o $launcherPublishRoot
if ($LASTEXITCODE -ne 0) { throw "启动器发布失败，退出码 $LASTEXITCODE。" }
Copy-Item -LiteralPath (Join-Path $launcherPublishRoot 'VRCTranslate.exe') -Destination $launcherPath

Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\使用说明.txt') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $noticeDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'DISCLAIMER.md') -Destination $noticeDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $noticeDirectory

Write-Host '[4/9] 检查目录层级、模型、版本与本机凭据'
Assert-CleanPackage -Root $packageDirectory

Write-Host '[5/9] 验证主程序与根目录启动器'
& pwsh -NoProfile -ExecutionPolicy Bypass -File $releaseSmokeScript `
    -Executable $internalExecutable `
    -ExpectedVersion $version
if ($LASTEXITCODE -ne 0) { throw "主程序冒烟测试失败，退出码 $LASTEXITCODE。" }
& pwsh -NoProfile -ExecutionPolicy Bypass -File $launcherSmokeScript -Launcher $launcherPath
if ($LASTEXITCODE -ne 0) { throw "启动器冒烟测试失败，退出码 $LASTEXITCODE。" }
Assert-CleanPackage -Root $packageDirectory

Write-Host '[6/9] 生成 ZIP 与 SHA-256'
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$zipHash *$([System.IO.Path]::GetFileName($zipPath))`n"
[System.IO.File]::WriteAllText(
    $checksumPath,
    $checksumLine,
    [System.Text.UTF8Encoding]::new($false))

Write-Host '[7/9] 解压 ZIP 并复核结构与文件哈希'
Assert-SafeWorkspacePath -Path $zipCheckRoot -Expected (Join-Path $repoRoot 'artifacts\release-zip-check')
if (Test-Path -LiteralPath $zipCheckRoot) {
    Remove-Item -LiteralPath $zipCheckRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $zipCheckRoot -Force | Out-Null
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $zipCheckRoot
    $extractedDirectory = Join-Path $zipCheckRoot $packageName
    Assert-CleanPackage -Root $extractedDirectory
    foreach ($relative in @('VRCTranslate.exe', '程序文件\VrcTranslate.exe')) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $packageDirectory $relative) -Algorithm SHA256).Hash
        $extractedHash = (Get-FileHash -LiteralPath (Join-Path $extractedDirectory $relative) -Algorithm SHA256).Hash
        if ($sourceHash -ne $extractedHash) {
            throw "ZIP 解压后的 $relative 与源发布目录不一致。"
        }
    }

    Write-Host '[8/9] 验证 ZIP 解压后的主程序'
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $releaseSmokeScript `
        -Executable (Join-Path $extractedDirectory '程序文件\VrcTranslate.exe') `
        -ExpectedVersion $version
    if ($LASTEXITCODE -ne 0) { throw "ZIP 解压版主程序冒烟测试失败，退出码 $LASTEXITCODE。" }

    Write-Host '[9/9] 验证 ZIP 首次启动不导入本机密钥'
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $launcherSmokeScript `
        -Launcher (Join-Path $extractedDirectory 'VRCTranslate.exe') `
        -UsePortableData
    if ($LASTEXITCODE -ne 0) { throw "ZIP 首次启动测试失败，退出码 $LASTEXITCODE。" }
    Assert-NoKnownLocalSecrets -Root $extractedDirectory
}
finally {
    if (Test-Path -LiteralPath $zipCheckRoot) {
        Remove-Item -LiteralPath $zipCheckRoot -Recurse -Force
    }
}

Assert-CleanPackage -Root $packageDirectory
$packageBytes = (Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
    Measure-Object -Property Length -Sum).Sum
$zipBytes = (Get-Item -LiteralPath $zipPath).Length
Write-Host ''
Write-Host '正式发布包已生成并通过验证：' -ForegroundColor Green
Write-Host "  启动程序：$launcherPath"
Write-Host "  ZIP：$zipPath"
Write-Host "  SHA-256：$zipHash"
Write-Host ("  大小：目录 {0:N1} MiB，ZIP {1:N1} MiB" -f ($packageBytes / 1MB), ($zipBytes / 1MB))
