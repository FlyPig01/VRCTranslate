<#
    Runs the translation-quality comparison against a configured VRCTranslate
    profile file. The profile file stays outside this repository; only dataset
    text, translations, latency and errors are written under results/.
#>
param(
    [string] $Profiles = 'D:\soft\VRCTranslate-v1.0.2-win-x64\data\v2-profiles.json',
    [string] $Tag = (Get-Date -Format 'yyyyMMdd-HHmm'),
    [string] $Python = 'python',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$harness = Join-Path $root 'harness\TranslationQualityHarness.csproj'
$baseline = Join-Path $root 'datasets\baseline-27.json'
$conversation = Join-Path $root 'datasets\conversation-20.json'
$glossary = Join-Path $root '..\..\src\VrcTranslate.Desktop\Resources\default-glossary.json'
$results = Join-Path $root 'results'
$raw = Join-Path $results ("raw-$Tag.json")

if (-not (Test-Path -LiteralPath $Profiles -PathType Leaf)) {
    throw "找不到 API 配置文件：$Profiles。请通过 -Profiles 指定 v2-profiles.json。"
}

New-Item -ItemType Directory -Force -Path $results | Out-Null
dotnet run --project $harness -c $Configuration -p:Platform=x64 -- `
    --profiles $Profiles `
    --baseline $baseline `
    --conversation $conversation `
    --glossary $glossary `
    --out $raw
if ($LASTEXITCODE -ne 0) { throw "翻译测试失败，退出码 $LASTEXITCODE。" }

& $Python (Join-Path $root 'score.py') --raw $raw --out-dir $results
if ($LASTEXITCODE -ne 0) { throw "评分失败，退出码 $LASTEXITCODE。" }

Write-Host "原始结果：$raw"
Write-Host "汇总：$(Join-Path $results 'summary.md')"
Write-Host "逐条对照：$(Join-Path $results 'examples.md')"
