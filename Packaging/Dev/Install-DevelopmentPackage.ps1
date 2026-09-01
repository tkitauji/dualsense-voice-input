param(
  [string]$PackagePath = (Join-Path $PSScriptRoot 'DualSenseVoice-dev-signed-v1.0.0-x64.msix'),
  [string]$CertificatePath = (Join-Path $PSScriptRoot 'DualSenseVoice-dev-signing-certificate.cer'),
  [string]$DependencyPath
)

$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$CertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
if (-not [string]::IsNullOrWhiteSpace($DependencyPath)) {
  $DependencyPath = (Resolve-Path -LiteralPath $DependencyPath).Path
}
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
$rootStorePath = "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
$wasAlreadyTrusted = Test-Path -LiteralPath $rootStorePath

Write-Host 'DualSense Voice development package installer' -ForegroundColor Cyan
Write-Warning 'This is a development-only package, not the Microsoft Store-signed release.'
Write-Host 'Windows will ask whether to trust the temporary development certificate.'
$confirmation = Read-Host 'Type INSTALL to continue'
if ($confirmation -cne 'INSTALL') { Write-Host 'Cancelled.'; exit 1 }

$installedVclibs = @(Get-AppxPackage -Name 'Microsoft.VCLibs.140.00.UWPDesktop' -ErrorAction SilentlyContinue)
if ($installedVclibs.Count -eq 0 -and [string]::IsNullOrWhiteSpace($DependencyPath)) {
  throw @'
Microsoft VC++ Desktop framework is required for this development package.
Download the official x64 package from:
https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx
Then rerun this script with -DependencyPath <downloaded-appx-path>.
The Microsoft Store release installs this declared dependency automatically.
'@
}

try {
  if (-not $wasAlreadyTrusted) {
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
  }
  if ([string]::IsNullOrWhiteSpace($DependencyPath)) {
    Add-AppxPackage -Path $PackagePath
  }
  else {
    Add-AppxPackage -Path $PackagePath -DependencyPath $DependencyPath
  }
  Write-Host 'Installed. Launch DualSense Voice from the Start menu.' -ForegroundColor Green
}
finally {
  # Trust is needed only while Windows validates the package installation.
  if (-not $wasAlreadyTrusted -and (Test-Path -LiteralPath $rootStorePath)) {
    Remove-Item -LiteralPath $rootStorePath -Force
  }
}
