# Places the bundled local speech model into v2\assets\models\speech so the
# next build/publish ships it inside the package. The file is re-downloadable
# and therefore not stored in source control; run this once per clone.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tests\Import-BundledSpeechModel.ps1
#   powershell -ExecutionPolicy Bypass -File tests\Import-BundledSpeechModel.ps1 -Force
[CmdletBinding()]
param(
    # Overwrite an existing (possibly corrupt) local copy.
    [switch] $Force,

    # Override the destination file path.
    [string] $Destination = (Join-Path $PSScriptRoot '..\assets\models/speech/ggml-base-q5_1.bin')
)

$ErrorActionPreference = 'Stop'

$fileName = 'ggml-base-q5_1.bin'
$minimumBytes = 50_000_000   # A real base q5_1 model is ~57 MB.
$ggmlMagic = [byte[]] (0x6C, 0x6D, 0x67, 0x67)  # 'lmgg' = 0x67676d6c little-endian.

# hf-mirror.com is reachable from mainland China; huggingface.co is the fallback.
$sources = @(
    'https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin',
    'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin'
)

function Test-ModelFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -lt $minimumBytes) { return $false }
    try {
        $head = [System.IO.File]::ReadAllBytes($Path)[0..3]
        for ($i = 0; $i -lt 4; $i++) {
            if ($head[$i] -ne $ggmlMagic[$i]) { return $false }
        }
        return $true
    }
    catch {
        return $false
    }
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if (-not $Force -and (Test-ModelFile $destinationPath)) {
    Write-Host "语音模型已存在且校验通过：$destinationPath"
    return
}

$downloadDirectory = Split-Path -Parent $destinationPath
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
$temporaryPath = "$destinationPath.download"

$lastError = $null
foreach ($source in $sources) {
    try {
        Write-Host "正在从 $source 下载 $fileName ..."
        Invoke-WebRequest -Uri $source -OutFile $temporaryPath -UseBasicParsing
        if (Test-ModelFile $temporaryPath) {
            Move-Item -Force -Path $temporaryPath -Destination $destinationPath
            Write-Host "完成：$destinationPath ($((Get-Item -LiteralPath $destinationPath).Length) 字节)"
            return
        }
        $lastError = "$source 返回的内容不是有效的 Whisper GGML 模型。"
        Write-Warning $lastError
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $temporaryPath
    }
    catch {
        $lastError = $_.Exception.Message
        Write-Warning "下载失败：$lastError"
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $temporaryPath
    }
}

throw "无法获取 $fileName。最后一个错误：$lastError"
