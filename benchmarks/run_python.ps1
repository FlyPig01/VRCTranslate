param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Script,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ScriptArgs
)

$Root = Split-Path -Parent $PSScriptRoot
$BenchRoot = $PSScriptRoot
$env:PYTHONUTF8 = "1"
$env:PYTHONPATH = "$Root\src;$Root\.venv\Lib\site-packages;$Root"
$env:HF_HOME = "$BenchRoot\cache\huggingface"
$env:HUGGINGFACE_HUB_CACHE = "$BenchRoot\cache\huggingface\hub"
$env:PIP_CACHE_DIR = "$BenchRoot\cache\pip"
$env:TEMP = "$BenchRoot\tmp"
$env:TMP = "$BenchRoot\tmp"
$env:MPLCONFIGDIR = "$BenchRoot\cache\matplotlib"

& "$BenchRoot\.venv\Scripts\python.exe" $Script @ScriptArgs
exit $LASTEXITCODE
