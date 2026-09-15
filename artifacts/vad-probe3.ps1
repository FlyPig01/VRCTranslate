$ErrorActionPreference = 'Stop'
$appDir = 'E:\MyTools\VRCTranslate\artifacts\manual-test'
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Core.dll'))
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Application.dll'))
$infra = [System.Reflection.Assembly]::LoadFrom((Join-Path $appDir 'VrcTranslate.Infrastructure.dll'))
$type = $infra.GetType('VrcTranslate.Infrastructure.Speech.SileroVadSegmenter')
$model = Join-Path $appDir 'Models\vad\silero_vad.onnx'
function Read-Wav([string]$path) {
    $fs = [System.IO.File]::OpenRead($path); $br = New-Object System.IO.BinaryReader($fs)
    [void]$br.ReadBytes(44); $bytes = $br.ReadBytes([int]($fs.Length - 44)); $fs.Dispose()
    $a = New-Object 'single[]' ([int]($bytes.Length / 2))
    for ($i = 0; $i -lt $a.Length; $i++) { $a[$i] = [single]([System.BitConverter]::ToInt16($bytes, $i * 2) / 32768.0) }
    return $a
}
foreach ($dur in @(18, 24, 30)) {
    $samples = Read-Wav ('E:\MyTools\VRCTranslate\artifacts\vad-probe-' + $dur + '.wav')
    $seg = $type::new($model, 16000, [single]0.0, [single]0.24, [single]0.6, [single]12.0, [single]60.0)
    $arrays = New-Object System.Collections.ArrayList
    for ($off = 0; $off + 512 -le $samples.Length; $off += 512) {
        $chunk = New-Object 'single[]' 512; [Array]::Copy($samples, $off, $chunk, 0, 512)
        foreach ($s in $seg.Append([System.ReadOnlyMemory[single]]$chunk)) { [void]$arrays.Add($s.ToArray()) }
    }
    foreach ($s in $seg.Flush()) { [void]$arrays.Add($s.ToArray()) }
    '=== ' + $dur + ' s 输入 → ' + $arrays.Count + ' 段 ==='
    $cursor = 0; $prevEnd = -1; $maxOver = 0
    for ($i = 0; $i -lt $arrays.Count; $i++) {
        $a = $arrays[$i]; $len = $a.Length
        $found = -1
        for ($p = $cursor; $p -le $samples.Length - 32; $p++) {
            $ok = $true
            for ($k = 0; $k -lt 32; $k++) { if ($samples[$p + $k] -ne $a[$k]) { $ok = $false; break } }
            if ($ok) { $found = $p; break }
        }
        if ($found -lt 0) { '  段 ' + ($i+1) + ': 长度 ' + $len + '，未能定位'; continue }
        $end = $found + $len
        $gap = if ($prevEnd -ge 0) { $found - $prevEnd } else { $found }
        $over = $len - 192000
        if ($over -gt $maxOver) { $maxOver = $over }
        '  段 {0}: 起 {1,7} 终 {2,7} 长 {3,7} ({4,5:N2} s) 超 12 s {5,4} 样本  缺口 {6,5}' -f ($i+1), $found, $end, $len, ($len/16000), $over, $gap
        $prevEnd = $end; $cursor = $found + 1
    }
    '  末尾缺口 ' + ($samples.Length - $prevEnd) + ' 样本；最长段超出 12 s ' + $maxOver + ' 样本 = 窗口的 ' + [math]::Round($maxOver / 512, 2) + ' 倍'
    $seg.Dispose()
}
