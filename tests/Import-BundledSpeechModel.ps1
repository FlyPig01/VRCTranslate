# Places the bundled local speech models into assets/models/speech so the next
# build/publish ships them inside the package. The payloads are re-downloadable
# and therefore not stored in source control; run this once per clone.
#
#   sensevoice/  SenseVoiceSmall INT8 recognition (model.int8.onnx + tokens.txt)
#   speaker/     speaker separation for the caption speaker labels:
#                  segmentation.int8.onnx  pyannote segmentation 3.0 (int8)
#                  embedding.onnx         3D-Speaker CAM++ speaker embedding (zh+en)
#   vad/         silero_vad.onnx — Silero VAD gate that separates voice from
#                game sound (GitHub release asset, not on the HF mirrors)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1 -Force
#   powershell -ExecutionPolicy Bypass -File tests/Import-BundledSpeechModel.ps1 -Source https://my-mirror.example/
[CmdletBinding()]
param(
    # Re-download even when the local copy already verifies.
    [switch] $Force,

    # Override the destination folder. Defaults to assets/models/speech next to
    # this script.
    [string] $Destination,

    # Replace the mirror host (must end with a slash). The built-in order is
    # hf-mirror.com first because it is reachable from mainland China, then
    # huggingface.co.
    [string] $Source
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Destination = Join-Path $scriptDirectory '../assets/models/speech'
}

$senseVoiceRepo = 'csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/'
$segmentationRepo = 'csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/'
$embeddingRepo = 'csukuangfj/speaker-embedding-models/resolve/main/'

# Every file is pinned to the exact size and SHA-256 published by the upstream
# revision it comes from, so a truncated download or an HTML error page can
# never be mistaken for a model.
$payload = @(
    [pscustomobject]@{
        Name   = 'model.int8.onnx'
        Folder = 'sensevoice'
        Remote = 'model.int8.onnx'
        Repo   = $senseVoiceRepo
        Bytes  = 239233841
        Sha256 = 'C71F0CE00BEC95B07744E116345E33D8CBBE08CEF896382CF907BF4B51A2CD51'
    },
    [pscustomobject]@{
        Name   = 'tokens.txt'
        Folder = 'sensevoice'
        Remote = 'tokens.txt'
        Repo   = $senseVoiceRepo
        Bytes  = 315894
        Sha256 = 'F449EB28DC567533D7FA59BE34E2ABCA8784F771850C78A47FB731A31429A1DC'
    },
    [pscustomobject]@{
        Name   = 'segmentation.int8.onnx'
        Folder = 'speaker'
        Remote = 'model.int8.onnx'
        Repo   = $segmentationRepo
        Bytes  = 1540506
        Sha256 = 'D582F4B4C6B48205DE7E0643C57DF0DF5615A3C176189BE3FC461E9D18827B5D'
    },
    [pscustomobject]@{
        Name   = 'embedding.onnx'
        Folder = 'speaker'
        Remote = '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
        Repo   = $embeddingRepo
        Bytes  = 28281164
        Sha256 = 'AA3CFC16963A10586A9393F5035D6D6B57E98D358B347F80C2A30BF4F00CEBA2'
    },
    [pscustomobject]@{
        Name   = 'silero_vad.onnx'
        Folder = 'vad'
        Remote = 'silero_vad.onnx'
        Repo   = 'k2-fsa/sherpa-onnx/releases/download/asr-models/'
        Hosts  = @('https://github.com/')
        Bytes  = 643854
        Sha256 = '9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6'
    }
)

$repositoryHosts = if ($Source) { @($Source) } else { @('https://hf-mirror.com/', 'https://huggingface.co/') }

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

function Get-PayloadFile([string] $Root, $Entry, [string[]] $Hosts) {
    $directory = Join-Path $Root $Entry.Folder
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $finalPath = Join-Path $directory $Entry.Name
    $temporaryPath = $finalPath + '.download'
    $lastError = $null

    foreach ($mirror in $Hosts) {
        $uri = $mirror + $Entry.Repo + $Entry.Remote
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
    $path = Join-Path (Join-Path $destinationPath $entry.Folder) $entry.Name
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

foreach ($entry in $missing) {
    # Entries can override the mirror list; only the VAD gate lives on GitHub.
    $hosts = if ($entry.Hosts) { $entry.Hosts } else { $repositoryHosts }
    $null = Get-PayloadFile $destinationPath $entry $hosts
}

# Re-verify the whole payload so a partial run cannot leave a half-usable
# folder behind for the build to pick up.
foreach ($entry in $payload) {
    $reason = ''
    $path = Join-Path (Join-Path $destinationPath $entry.Folder) $entry.Name
    if (-not (Test-PayloadFile $path $entry ([ref] $reason))) {
        throw "语音模型安装后校验失败：$($entry.Name) — $reason"
    }
}

Write-Host "语音模型已就绪：$destinationPath"