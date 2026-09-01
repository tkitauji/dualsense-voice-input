$ErrorActionPreference = 'Stop'
$packages = @(Get-AppxPackage -Name 'DualSenseVoice.Dev')
if ($packages.Count -eq 0) { Write-Host 'DualSense Voice development package is not installed.'; exit 0 }
$packages | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
Write-Host 'DualSense Voice development package was removed.' -ForegroundColor Green
