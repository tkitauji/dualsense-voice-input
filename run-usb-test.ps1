#requires -Version 5.1

[CmdletBinding()]
param(
  [ValidateRange(0, 300)]
  [int]$WaitForControllerSeconds = 60,

  [string]$ReportDirectory,

  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
  $ReportDirectory = Join-Path $PSScriptRoot 'artifacts\hardware'
}

function Get-UsbDualSenseDevice {
  @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match '^(USB|HID)\\VID_054C&PID_0CE6'
  })
}

if (Get-Process -Name 'DualSenseVoice' -ErrorAction SilentlyContinue) {
  throw 'Close DualSense Voice before this test; the app and probe cannot capture the same USB microphone concurrently.'
}

$deadline = (Get-Date).AddSeconds($WaitForControllerSeconds)
$controllers = @(Get-UsbDualSenseDevice)
while ($controllers.Count -eq 0 -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 500
  $controllers = @(Get-UsbDualSenseDevice)
}
if ($controllers.Count -eq 0) {
  throw "A USB-connected DualSense was not found. Use a data-capable cable and retry. Waited $WaitForControllerSeconds seconds."
}

$probeProject = Join-Path $PSScriptRoot 'tools\DualSenseUsbHardwareProbe\DualSenseUsbHardwareProbe.csproj'
if (-not (Test-Path -LiteralPath $probeProject -PathType Leaf)) {
  throw "Probe project was not found: $probeProject"
}

$reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$report = Join-Path $reportRoot ('usb-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
if (Test-Path -LiteralPath $report) {
  throw "The report path already exists: $report"
}

$metadata = @(
  'DUALSENSE_VOICE_USB_REPORT|version=1'
  "TIMESTAMP|$([DateTimeOffset]::Now.ToString('o'))"
  "OS|$([Environment]::OSVersion.VersionString)"
  "CONTROLLERS|$($controllers.Count)"
)
$metadata | Set-Content -LiteralPath $report -Encoding UTF8

if (-not $NoBuild) {
  & dotnet build $probeProject --configuration Release --nologo 2>&1 |
    Tee-Object -FilePath $report -Append
  if ($LASTEXITCODE -ne 0) {
    throw "The USB probe build failed with exit code $LASTEXITCODE. Report: $report"
  }
}

& dotnet run --project $probeProject --configuration Release --no-build 2>&1 |
  Tee-Object -FilePath $report -Append
$probeExitCode = $LASTEXITCODE

if ($probeExitCode -ne 0) {
  throw "The USB hardware test failed with exit code $probeExitCode. Report: $report"
}

$passingResult = Select-String -LiteralPath $report -SimpleMatch -Pattern `
  'RESULT|USB standard microphone and physical mute-button capture passed.'
if (-not $passingResult) {
  throw "The probe exited successfully without its required passing result. Report: $report"
}

Write-Output "PASS|USB|$report"
