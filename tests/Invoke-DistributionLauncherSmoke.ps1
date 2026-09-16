param(
    [Parameter(Mandatory)]
    [string] $Launcher,
    [switch] $UsePortableData
)

$ErrorActionPreference = 'Stop'
$Launcher = (Resolve-Path -LiteralPath $Launcher).Path
$productRoot = Split-Path -Parent $Launcher
$temporaryData = Join-Path ([System.IO.Path]::GetTempPath()) (
    'VRCTranslate-launcher-smoke-' + [Guid]::NewGuid().ToString('N'))
$dataDirectory = if ($UsePortableData) { Join-Path $productRoot 'data' } else { $temporaryData }
$previousDataDirectory = [Environment]::GetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', 'Process')
$before = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (Test-Path -LiteralPath $dataDirectory) {
    throw "Launcher smoke requires a clean data directory: $dataDirectory"
}
if (-not $UsePortableData) {
    New-Item -ItemType Directory -Path $temporaryData -Force | Out-Null
    [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $temporaryData, 'Process')
}
else {
    [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $null, 'Process')
}

$launcherProcess = $null
$applicationProcess = $null
try
{
    $launcherProcess = Start-Process -FilePath $Launcher -WorkingDirectory $productRoot -PassThru
    if (-not $launcherProcess.WaitForExit(5000)) {
        throw 'Distribution launcher did not exit after starting the application.'
    }
    if ($launcherProcess.ExitCode -ne 0) {
        throw "Distribution launcher exited with code $($launcherProcess.ExitCode)."
    }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $window = $null
    for ($attempt = 0; $attempt -lt 60 -and $null -eq $window; $attempt++)
    {
        Start-Sleep -Milliseconds 200
        $window = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)) |
            Where-Object {
                $_.Current.Name -eq 'VRCTranslate' -and
                $_.Current.ProcessId -notin $before
            } |
            Select-Object -First 1
    }
    if ($null -eq $window) { throw 'Distribution launcher did not open the VRCTranslate window.' }

    $applicationProcess = Get-Process -Id $window.Current.ProcessId -ErrorAction Stop
    $actualPath = $applicationProcess.MainModule.FileName
    $expectedPath = Join-Path $productRoot '程序文件\VrcTranslate.exe'
    if (-not $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Launcher opened '$actualPath' instead of '$expectedPath'."
    }

    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $applicationProcess.WaitForExit(8000)) {
        throw 'Application launched from the distribution did not exit after closing its main window.'
    }

    if ($UsePortableData) {
        if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
            throw 'The launcher did not create data beside itself.'
        }
        if (Test-Path -LiteralPath (Join-Path $productRoot '程序文件\data')) {
            throw 'The internal program directory must not receive user data.'
        }
        $profilePath = Join-Path $dataDirectory 'v2-profiles.json'
        if (Test-Path -LiteralPath $profilePath) {
            $document = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
            $profiles = @($document.Profiles)
            $unexpected = @($profiles | Where-Object {
                $_.provider -ne 'echo' -or $_.id -ne 'local-test'
            })
            if ($unexpected.Count -gt 0) {
                throw 'A clean launch imported translation profiles from another installation.'
            }
        }
    }

    Write-Host 'Distribution launcher smoke passed with clean isolated data.' -ForegroundColor Green
}
finally
{
    if ($null -ne $applicationProcess -and -not $applicationProcess.HasExited) {
        Stop-Process -Id $applicationProcess.Id -Force
    }
    [Environment]::SetEnvironmentVariable('VRC_TRANSLATE_DATA_DIR', $previousDataDirectory, 'Process')
    if (-not $UsePortableData) {
        Remove-Item -LiteralPath $temporaryData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
