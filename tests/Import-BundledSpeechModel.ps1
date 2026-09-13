# Places the bundled local speech model (SenseVoiceSmall INT8 in the
# sherpa-onnx layout: model.int8.onnx + tokens.txt) into
# assets/models/speech/sensevoice so the next build/publish ships it inside the
# package. The payload is re-downloadable and therefore not stored in source
# control; run this once per clone.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1 -Force
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1 -Source https://my-mirror.example/sensevoice/
[CmdletBinding()]
param(
    # Re-download even when the local copy already verifies.
    [switch] $Force,

    # Override the destination folder. Defaults to assets/models/speech/sensevoice
    # next to this script.
    [string] $Destination,

    # Override the download base URL (must end with a slash). The built-in
    # mirrors are hf-mirror.com first because it is reachable from mainland
    # China, then huggingface.co.
    [string] $Source
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Destination = Join-Path $scriptDirectory '../assets/models/speech/sensevoice'
}

# Pinned upstream revision: csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.
# Both files are verified against the exact size and SHA-256 published by that
# revision, so a truncated download or an HTML error page can never be mistaken
# for a model.
$payload = @(
    [pscustomobject]@{
        Name   = 'model.int8.onnx'
        Bytes  = 239233841
        Sha256 = 'C71F0CE00BEC95B07744E116345E33D8CBBE08CEF896382CF907BF4B51A2CD51'
    },
    [pscustomobject]@{
        Name   = 'tokens.txt'
        Bytes  = 315894
        Sha256 = 'F449EB28DC567533D7FA59BE34E2ABCA8784F771850C78A47FB731A31429A1DC'
    }
)

$repositoryPath = 'csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/'
if ($Source) {
    $sources = @($Source)
}
else {
    $sources = @(
        ('https://hf-mirror.com/' + $repositoryPath),
        ('https://huggingface.co/' + $repositoryPath)
    )
}

# PowerShell 5.1 still defaults to TLS 1.0 on some machines.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Test-PayloadFile([string] $Path, $Entry, [ref] $Reason) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $Reason.Value = '文件不存在'
        return $false
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $Entry.Bytes) {
        $Reason.Value = "大小不符（期望 $($Entry.Bytes) 字节，实际 $($file.Length) 字节）"
        return $false
    }

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -ne $Entry.Sha256) {
        $Reason.Value = "SHA-256 不符（期望 $($Entry.Sha256)，实际 $hash）"
        return $false
    }

    return $true
}

function Get-PayloadFile([string] $Directory, $Entry, [string[]] $Mirrors) {
    $finalPath = Join-Path $Directory $Entry.Name
    $temporaryPath = $finalPath + '.download'
    $lastError = $null

    foreach ($mirror in $Mirrors) {
        $uri = $mirror + $Entry.Name
        try {
            Write-Host "正在从 $uri 下载 $($Entry.Name)（$([math]::Round($Entry.Bytes / 1MB, 1)) MB）…"
            $client = New-Object System.Net.WebClient
            try {
                $client.DownloadFile($uri, $temporaryPath)
            }
            finally {
                $client.Dispose()
            }

            $reason = ''
            if (Test-PayloadFile $temporaryPath $Entry ([ref] $reason)) {
                Move-Item -Force -Path $temporaryPath -Destination $finalPath
                Write-Host "完成：$finalPath"
                return $true
            }

            $lastError = "$uri 返回的内容校验失败：$reason"
            Write-Warning $lastError
        }
        catch {
            $lastError = $_.Exception.Message
            Write-Warning "从 $uri 下载失败：$lastError"
        }
        finally {
            Remove-Item -Force -ErrorAction SilentlyContinue -Path $temporaryPath
        }
    }

    throw "无法获取 $($Entry.Name)。最后一个错误：$lastError"
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$missing = @()
foreach ($entry in $payload) {
    $reason = ''
    $path = Join-Path $destinationPath $entry.Name
    if (-not $Force -and (Test-PayloadFile $path $entry ([ref] $reason))) {
        Write-Host "已存在且校验通过：$path"
        continue
    }

    if (-not $Force -and $reason -ne '文件不存在') { Write-Warning "$($entry.Name)：$reason，将重新下载。" }
    $missing += $entry
}

if ($missing.Count -eq 0) {
    Write-Host "语音模型已就绪：$destinationPath"
    return
}

New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
foreach ($entry in $missing) {
    $null = Get-PayloadFile $destinationPath $entry $sources
}

# Re-verify the whole payload so a partial run cannot leave a half-usable
# folder behind for the build to pick up.
foreach ($entry in $payload) {
    $reason = ''
    if (-not (Test-PayloadFile (Join-Path $destinationPath $entry.Name) $entry ([ref] $reason))) {
        throw "语音模型安装后校验失败：$($entry.Name) — $reason"
    }
}

Write-Host "语音模型已就绪：$destinationPath"
