#requires -Version 5.1

[CmdletBinding()]
param(
  [ValidateSet('Baseline', 'Steam', 'FF14')]
  [string]$Scenario = 'Baseline',

  [ValidateRange(0, 300)]
  [int]$WaitForControllerSeconds = 60,

  [string]$ReportDirectory,

  [string]$SteamGameProcessName = 'MonsterHunterWilds',

  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
  $ReportDirectory = Join-Path $PSScriptRoot 'artifacts\hardware'
}

function Get-DualSenseDevice {
  @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match 'VID_054C.*PID_0CE6'
  })
}

if (Get-Process -Name 'DualSenseVoice' -ErrorAction SilentlyContinue) {
  throw 'Close DualSense Voice before this test; the app and probe must not send two microphone clocks to one controller.'
}

$SteamGameProcessName = [System.IO.Path]::GetFileNameWithoutExtension($SteamGameProcessName)
if ([string]::IsNullOrWhiteSpace($SteamGameProcessName)) {
  throw 'SteamGameProcessName must identify the running Steam game executable.'
}
$steamRunning = [bool](Get-Process -Name 'steam' -ErrorAction SilentlyContinue)
$steamGameRunning = [bool](Get-Process -Name $SteamGameProcessName -ErrorAction SilentlyContinue)
$ff14Running = [bool](Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue)

switch ($Scenario) {
  'Baseline' {
    if ($steamRunning -or $steamGameRunning -or $ff14Running) {
      throw 'Baseline requires Steam, the Steam game, and FF14 to be closed.'
    }
  }
  'Steam' {
    if (-not $steamRunning) {
      throw 'Steam is not running. Start Steam and enable Steam Input for DualSense.'
    }
    if (-not $steamGameRunning) {
      throw "The Steam game process '$SteamGameProcessName' is not running. Start the game and enter gameplay before retrying."
    }
  }
  'FF14' {
    if (-not $ff14Running) {
      throw 'FF14 DirectX 11 is not running. Enter the game and run this scenario again.'
    }
  }
}

$deadline = (Get-Date).AddSeconds($WaitForControllerSeconds)
$controllers = @(Get-DualSenseDevice)
while ($controllers.Count -eq 0 -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 500
  $controllers = @(Get-DualSenseDevice)
}
if ($controllers.Count -eq 0) {
  throw "DualSense is not connected. Press the PS button or reconnect it, then retry. Waited $WaitForControllerSeconds seconds."
}

$probeProject = Join-Path $PSScriptRoot 'tools\GameInputInterferenceProbe\GameInputInterferenceProbe.csproj'
if (-not (Test-Path -LiteralPath $probeProject -PathType Leaf)) {
  throw "Probe project was not found: $probeProject"
}

$reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$report = Join-Path $reportRoot ('coexistence-{0}-{1:yyyyMMdd-HHmmss}.log' -f $Scenario, (Get-Date))
if (Test-Path -LiteralPath $report) {
  throw "The report path already exists: $report"
}

$metadata = @(
  'DUALSENSE_VOICE_COEXISTENCE_REPORT|version=2'
  "TIMESTAMP|$([DateTimeOffset]::Now.ToString('o'))"
  "SCENARIO|$Scenario"
  "OS|$([Environment]::OSVersion.VersionString)"
  "STEAM_RUNNING|$steamRunning"
  "STEAM_GAME_PROCESS|$SteamGameProcessName"
  "STEAM_GAME_RUNNING|$steamGameRunning"
  "FF14_RUNNING|$ff14Running"
  "CONTROLLERS|$($controllers.Count)"
)
$metadata | Set-Content -LiteralPath $report -Encoding UTF8

if (-not $NoBuild) {
  & dotnet build $probeProject --configuration Release --nologo 2>&1 |
    Tee-Object -FilePath $report -Append
  if ($LASTEXITCODE -ne 0) {
    throw "The coexistence probe build failed with exit code $LASTEXITCODE. Report: $report"
  }
}

& dotnet run --project $probeProject --configuration Release --no-build 2>&1 |
  Tee-Object -FilePath $report -Append
$probeExitCode = $LASTEXITCODE

if ($probeExitCode -ne 0) {
  throw "The $Scenario coexistence test failed with exit code $probeExitCode. Report: $report"
}

$passingResult = Select-String -LiteralPath $report -SimpleMatch -Pattern `
  'RESULT|No WinMM or DirectInput interference observed while Bluetooth microphone audio was captured.'
if (-not $passingResult) {
  throw "The probe exited successfully without its required passing result. Report: $report"
}

Write-Output "PASS|$Scenario|$report"
