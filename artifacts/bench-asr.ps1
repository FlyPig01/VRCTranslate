param(
    [string]$Files = 'zh,ja,ko,en',
    [string]$Modes = 'auto',
    [int]$Samples = 10,
    [string]$Json = '',
    [switch]$Vad
)
$ErrorActionPreference = 'Stop'
$fileList = @($Files.Split([char[]]@(',', ';', ' '), [System.StringSplitOptions]::RemoveEmptyEntries))
$modeList = @($Modes.Split([char[]]@(',', ';', ' '), [System.StringSplitOptions]::RemoveEmptyEntries))
$appDir   = 'E:\MyTools\VRCTranslate\artifacts\manual-test'
$probeDir = 'E:\MyTools\VRCTranslate\artifacts\probe'
$models   = Join-Path $appDir 'Models'
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Core.dll'))
$appAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Application.dll'))
$infra  = [System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Infrastructure.dll'))
$catalogType = $infra.GetType('VrcTranslate.Infrastructure.Speech.LocalSpeechModelCatalog')
$managerType = $infra.GetType('VrcTranslate.Infrastructure.Speech.LocalSpeechModelManager')
$recogType   = $infra.GetType('VrcTranslate.Infrastructure.Speech.SenseVoiceSpeechRecognizer')
$speakerType = $infra.GetType('VrcTranslate.Infrastructure.Speech.LocalSpeakerIdentifier')
$serviceType = $appAsm.GetType('VrcTranslate.Application.Speech.LocalSpeechService')
$reqType     = [System.Type]::GetType('VrcTranslate.Core.Speech.SpeechRecognitionRequest, VrcTranslate.Core')
$recogDir   = Join-Path $models 'sensevoice'
$speakerDir = Join-Path $models 'speaker'
$recognizerManager = $managerType::new($catalogType::Recognition, $recogDir, $recogDir)
$speakerManager    = $managerType::new($catalogType::Speaker, $speakerDir, $speakerDir)
$recognizer        = $recogType::new($recognizerManager)
$speakerId         = $speakerType::new($speakerManager)
$service           = $serviceType::new($recognizerManager, $recognizer, $speakerId)
$cancel = [System.Threading.CancellationToken]::None

function Read-Wav([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        if ([System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4)) -ne 'RIFF') { throw "不是 WAV: $path" }
        [void]$br.ReadBytes(8)
        $fmtOk = $false; $rate = 0; $channels = 0; $bits = 0; $data = $null
        while ($fs.Position -lt $fs.Length - 1) {
            $id = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            $size = $br.ReadInt32()
            if ($id -eq 'fmt ') {
                $fmt = $br.ReadBytes($size)
                $channels = [System.BitConverter]::ToInt16($fmt, 2)
                $rate = [System.BitConverter]::ToInt32($fmt, 4)
                $bits = [System.BitConverter]::ToInt16($fmt, 14)
                $fmtOk = $true
            } elseif ($id -eq 'data') { $data = $br.ReadBytes($size) } else { [void]$br.ReadBytes($size) }
            if ($size % 2 -eq 1) { [void]$br.ReadByte() }
        }
        if (-not $fmtOk) { throw '缺少 fmt 块' }
        if ($bits -ne 16) { throw "只支持 16 位 PCM，实际 $bits 位" }
        $count = [int]($data.Length / 2)
        $samples = New-Object 'single[]' $count
        for ($i = 0; $i -lt $count; $i++) { $samples[$i] = [single]([System.BitConverter]::ToInt16($data, $i * 2) / 32768.0) }
        if ($channels -gt 1) {
            $mono = New-Object 'single[]' ([int]($count / $channels))
            for ($i = 0; $i -lt $mono.Length; $i++) {
                $sum = 0.0
                for ($c = 0; $c -lt $channels; $c++) { $sum += $samples[$i * $channels + $c] }
                $mono[$i] = [single]($sum / $channels)
            }
            $samples = $mono
        }
        return [pscustomobject]@{ Samples = $samples; Rate = $rate; Seconds = [math]::Round($samples.Length / $rate, 2) }
    } finally { $fs.Dispose() }
}

function Invoke-Once($wav, [string]$lang) {
    $req = $reqType::new([System.ReadOnlyMemory[float]]$wav.Samples, $wav.Rate, $lang, $null)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $res = $service.RecognizeAsync($req, $cancel).GetAwaiter().GetResult()
    $sw.Stop()
    return [pscustomobject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Text = $res.Text; Detected = $res.SourceLanguage }
}

$table = @{}
foreach ($f in $fileList) { $table[$f] = Read-Wav (Join-Path $probeDir "$f.wav") }
[void](Invoke-Once $table[$fileList[0]] 'auto')

# ---- D16-B2：走 VAD 的真实路径复测 ------------------------------------------------
# 与默认直喂模式的区别：音频先过 SileroVadSegmenter 切段（生产同款默认参数、512 一块含尾块），
# 逐段识别。输出段号/长度/识别文本，并报「不明去向样本占比」（方案 §5.3）。
#
# 【口径：别把下面几个数当成「丢了多少语音」】实测标定（2026-09-16，详见方案 §2.2/§5.4）：
#   · 「距上段终点」= 按定位算出的样本距离，**真实素材上主要是自然停顿与拼接静音**，
#     合成信号（连续正弦）上才是真正的强切边界损失（1280~1344 样本 = 80~84 ms）；
#     真实素材实测 12032~32896，**不要拿去和合成信号的 1536 容差比**。
#   · 「首段起点」≈ 1280 样本（80 ms）：检测器预热，不是丢失。
#   · 「末尾缺口」实测在 0 ~ 30848 样本（0 ~ 1.93 s）之间波动，取决于最后一窗的语音概率，
#     波动大属正常，**不要当成缺陷**。
#   · 「不明去向」= 喂入 − 产出，**主体是 VAD 有意丢掉的静音**（真实素材实测 25~31%，
#     合成信号 1.00%）；真正可能丢内容的只有强切边界 80~84 ms + 预热 80 ms。
if ($Vad) {
    $vadType = $infra.GetType('VrcTranslate.Infrastructure.Speech.SileroVadSegmenter')
    $vadModel = Join-Path $models 'vad\silero_vad.onnx'
    foreach ($f in $fileList) {
        $wav = $table[$f]
        if ($wav.Rate -ne 16000) { throw "$f 不是 16 kHz（VAD 只支持 16k/8k）" }
        $seg = $vadType::new($vadModel)
        $segments = New-Object System.Collections.ArrayList
        $pcm = $wav.Samples
        for ($off = 0; $off -lt $pcm.Length; $off += 512) {
            $size = [Math]::Min(512, $pcm.Length - $off)
            $chunk = New-Object 'single[]' $size
            [Array]::Copy($pcm, $off, $chunk, 0, $size)
            foreach ($s in $seg.Append([System.ReadOnlyMemory[single]]$chunk)) { [void]$segments.Add($s.ToArray()) }
        }
        foreach ($s in $seg.Flush()) { [void]$segments.Add($s.ToArray()) }
        $seg.Dispose()

        # 32 样本前缀 + 单调游标定位（周期信号下短前缀会匹配错位置，见方案第三版踩坑记录）
        $cursor = 0; $prevEnd = -1
        $produced = [long]0
        "=== $f（{0:N0} 样本 / {1:N2} s）→ {2} 段 ===" -f $pcm.Length, $wav.Seconds, $segments.Count
        $whole = Invoke-Once $wav 'auto'
        "  [直喂对照] {0,7:N1} ms 检测={1,-9} {2}" -f $whole.Ms, $whole.Detected, $whole.Text
        for ($i = 0; $i -lt $segments.Count; $i++) {
            $a = $segments[$i]; $len = $a.Length; $produced += $len
            $found = -1
            for ($p = $cursor; $p + 32 -le $pcm.Length; $p++) {
                $ok = $true
                for ($k = 0; $k -lt 32; $k++) { if ($pcm[$p + $k] -ne $a[$k]) { $ok = $false; break } }
                if ($ok) { $found = $p; break }
            }
            if ($found -lt 0) { "  段 {0}: 长 {1:N0}，未能定位" -f ($i + 1), $len; continue }
            # 首段报「起点偏移」（预热），其余段报「距上段终点」（≈静音长度，非损失）。
            $gap = if ($prevEnd -ge 0) { $found - $prevEnd } else { $found }
            $gapLabel = if ($prevEnd -ge 0) { '距上段终点' } else { '首段起点  ' }
            $segWav = [pscustomobject]@{ Samples = $a; Rate = 16000; Seconds = [Math]::Round($len / 16000.0, 2) }
            $one = Invoke-Once $segWav 'auto'
            "  段 {0}: 长 {1,7:N0}（{2,5:N2} s） {3} {4,6} 样本  {5,7:N1} ms 检测={6,-9} {7}" -f ($i + 1), $len, ($len / 16000.0), $gapLabel, $gap, $one.Ms, $one.Detected, $one.Text
            $prevEnd = $found + $len; $cursor = $found + 1
        }
        $unaccounted = $pcm.Length - $produced
        "  账目：喂入 {0:N0} 产出 {1:N0}  末尾未成段 {2:N0}  不明去向 {3:N0}（占喂入 {4:F2}%，主体是静音）" -f $pcm.Length, $produced, ($pcm.Length - $prevEnd), $unaccounted, ($unaccounted * 100.0 / $pcm.Length)
        ""
    }
    $service.DisposeAsync().AsTask().Wait()
    return
}

$rows = @()
foreach ($f in $fileList) {
    foreach ($mode in $modeList) {
        $lang = if ($mode -eq 'auto' -or $mode -eq '') { 'auto' } else { $mode }
        $times = @(); $text = ''; $detected = ''
        for ($i = 0; $i -lt $Samples; $i++) {
            $one = Invoke-Once $table[$f] $lang
            $times += $one.Ms
            if ($i -eq 0) { $text = $one.Text; $detected = $one.Detected }
        }
        $sorted = @($times | Sort-Object)
        $median = if ($sorted.Count % 2 -eq 1) { $sorted[[int](($sorted.Count - 1) / 2)] } else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
        $p90 = $sorted[[math]::Min($sorted.Count - 1, [int][math]::Ceiling($sorted.Count * 0.9) - 1)]
        $row = [pscustomobject]@{ file = $f; mode = $mode; detected = $detected; text = $text; ms_median = [math]::Round($median, 1); ms_p90 = [math]::Round($p90, 1); ms_min = [math]::Round($sorted[0], 1); ms_max = [math]::Round($sorted[$sorted.Count - 1], 1); samples = $Samples }
        $rows += $row
        '{0,-18} x {1,-5} 检测={2,-9} 中位 {3,6} ms  P90 {4,6} ms  |  {5}' -f $f, $mode, $detected, $row.ms_median, $row.ms_p90, $text
    }
}
if ($Json -ne '') { $rows | ConvertTo-Json -Depth 4 | Set-Content -Path $Json -Encoding UTF8 }
$rows | ConvertTo-Json -Depth 4 -Compress
$service.DisposeAsync().AsTask().Wait()
