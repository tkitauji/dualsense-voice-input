param(
  [string]$PackageName = 'DualSenseVoice.Dev',
  [string]$Publisher = 'CN=DualSenseVoiceDevelopment',
  [string]$PublisherDisplayName = 'DualSense Voice Development',
  [string]$Version = '1.0.0.0',
  [string]$Configuration = 'Release',
  [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'publish'
$layoutDirectory = Join-Path $artifactRoot 'msix-layout'
$packageDirectory = Join-Path $artifactRoot 'package'
$packagePath = Join-Path $packageDirectory "DualSenseVoice-$Version-x64.msix"
$msixToolsProject = Join-Path $projectRoot 'tools\MsixTools\MsixTools.csproj'

foreach ($path in @($publishDirectory, $layoutDirectory, $packageDirectory)) {
  if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
  New-Item -ItemType Directory -Path $path -Force | Out-Null
}

& $dotnetCommand.Source publish (Join-Path $projectRoot 'DualSenseVoice\DualSenseVoice.csproj') `
  -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

& $dotnetCommand.Source restore $msixToolsProject
if ($LASTEXITCODE -ne 0) { throw 'MSIX build tools restore failed.' }

Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $layoutDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'Packaging\Assets') -Destination $layoutDirectory -Recurse

# The selected package architecture is x64. Whisper.net ships all Windows CPU
# architectures, so omit the unused native runtimes from this architecture package.
foreach ($unusedRuntime in @('runtimes\win-x86', 'runtimes\win-arm64')) {
  $unusedPath = Join-Path $layoutDirectory $unusedRuntime
  if (Test-Path -LiteralPath $unusedPath) { Remove-Item -LiteralPath $unusedPath -Recurse -Force }
}

$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'Packaging\AppxManifest.xml.in') -Raw
$manifest = $manifest.Replace('__PACKAGE_NAME__', $PackageName)
$manifest = $manifest.Replace('__PUBLISHER__', $Publisher)
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName)
$manifest = $manifest.Replace('__VERSION__', $Version)
Set-Content -LiteralPath (Join-Path $layoutDirectory 'AppxManifest.xml') -Value $manifest -Encoding utf8NoBOM

$packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$makeAppx = Get-ChildItem -Path (Join-Path $packageCache 'microsoft.windows.sdk.buildtools') -Recurse -Filter 'makeappx.exe' |
  Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
  Sort-Object FullName -Descending |
  Select-Object -First 1
if (-not $makeAppx) { throw 'Microsoft.Windows.SDK.BuildTools restore succeeded, but makeappx.exe was not found.' }

& $makeAppx.FullName pack /o /d $layoutDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

Write-Output $packagePath
