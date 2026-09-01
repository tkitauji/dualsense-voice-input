param(
  [string]$PackagePath = (Join-Path $PSScriptRoot 'DualSenseVoice-dev-signed-v1.0.0-x64.msix'),
  [string]$CertificatePath = (Join-Path $PSScriptRoot 'DualSenseVoice-dev-signing-certificate.cer')
)

$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$CertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
$rootStorePath = "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
$wasAlreadyTrusted = Test-Path -LiteralPath $rootStorePath

Write-Host 'DualSense Voice development package installer' -ForegroundColor Cyan
Write-Warning 'This is a development-only package, not the Microsoft Store-signed release.'
Write-Host 'Windows will ask whether to trust the temporary development certificate.'
$confirmation = Read-Host 'Type INSTALL to continue'
if ($confirmation -cne 'INSTALL') { Write-Host 'Cancelled.'; exit 1 }

try {
  if (-not $wasAlreadyTrusted) {
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
  }
  Add-AppxPackage -Path $PackagePath
  Write-Host 'Installed. Launch DualSense Voice from the Start menu.' -ForegroundColor Green
}
finally {
  # Trust is needed only while Windows validates the package installation.
  if (-not $wasAlreadyTrusted -and (Test-Path -LiteralPath $rootStorePath)) {
    Remove-Item -LiteralPath $rootStorePath -Force
  }
}
