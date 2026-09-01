param(
  [string]$PackagePath = (Join-Path $PSScriptRoot 'artifacts\package\DualSenseVoice-1.0.0.0-x64.msix'),
  [string]$ReportPath = (Join-Path $PSScriptRoot ('artifacts\wack\DualSenseVoice-{0:yyyyMMdd-HHmmss}.xml' -f (Get-Date))),
  [string]$AppCertPath
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Windows App Certification Kit must be run from an Administrator PowerShell session.'
}

$package = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
  throw "MSIX package was not found: $package"
}

if ([string]::IsNullOrWhiteSpace($AppCertPath)) {
  $candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'),
    (Join-Path $env:ProgramFiles 'Windows Kits\10\App Certification Kit\appcert.exe')
  )
  $AppCertPath = $candidates | Where-Object {
    $_ -and (Test-Path -LiteralPath $_ -PathType Leaf)
  } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($AppCertPath) -or
    -not (Test-Path -LiteralPath $AppCertPath -PathType Leaf)) {
  throw 'appcert.exe was not found. Install the Windows App Certification Kit from the current Windows SDK.'
}

$report = [System.IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $report
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
if (Test-Path -LiteralPath $report) {
  throw "The report path already exists. Choose a new path: $report"
}

& $AppCertPath reset
if ($LASTEXITCODE -ne 0) {
  throw "appcert reset failed with exit code $LASTEXITCODE."
}

& $AppCertPath test -appxpackagepath $package -reportoutputpath $report
if ($LASTEXITCODE -ne 0) {
  throw "appcert test failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
  throw 'appcert reported success but did not create a report.'
}

Write-Output $report
