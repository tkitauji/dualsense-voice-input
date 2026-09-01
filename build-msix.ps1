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
$layoutAssets = Join-Path $layoutDirectory 'Assets'
New-Item -ItemType Directory -Path $layoutAssets -Force | Out-Null
foreach ($assetName in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
  Copy-Item -LiteralPath (Join-Path $projectRoot "Packaging\Assets\$assetName") -Destination $layoutAssets
}

$requiredNotices = @(
  'THIRD-PARTY-NOTICES.md',
  'Licenses\DOTNET-LICENSE.txt',
  'Licenses\DOTNET-THIRD-PARTY-NOTICES.txt',
  'Licenses\CONCENTUS-LICENSE.txt',
  'Licenses\HIDSHARP-LICENSE.txt',
  'Licenses\NAUDIO-LICENSE.txt',
  'Licenses\OPENAI-WHISPER-LICENSE.txt',
  'Licenses\WHISPER.CPP-LICENSE.txt',
  'Licenses\WHISPER.NET-LICENSE.txt'
)
foreach ($notice in $requiredNotices) {
  if (-not (Test-Path -LiteralPath (Join-Path $layoutDirectory $notice))) {
    throw "Required third-party notice is missing from the package layout: $notice"
  }
}

# The selected package architecture is x64. Whisper.net ships all Windows CPU
# architectures, so omit the unused native runtimes from this architecture package.
foreach ($unusedRuntime in @('runtimes\win-x86', 'runtimes\win-arm64')) {
  $unusedPath = Join-Path $layoutDirectory $unusedRuntime
  if (Test-Path -LiteralPath $unusedPath) { Remove-Item -LiteralPath $unusedPath -Recurse -Force }
}

$manifestTemplatePath = Join-Path $projectRoot 'Packaging\AppxManifest.xml.in'
$manifest = [System.IO.File]::ReadAllText($manifestTemplatePath, [System.Text.Encoding]::UTF8)
$manifest = $manifest.Replace('__PACKAGE_NAME__', $PackageName)
$manifest = $manifest.Replace('__PUBLISHER__', $Publisher)
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $PublisherDisplayName)
$manifest = $manifest.Replace('__VERSION__', $Version)
$manifestPath = Join-Path $layoutDirectory 'AppxManifest.xml'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

$packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$makeAppx = Get-ChildItem -Path (Join-Path $packageCache 'microsoft.windows.sdk.buildtools') -Recurse -Filter 'makeappx.exe' |
  Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
  Sort-Object FullName -Descending |
  Select-Object -First 1
if (-not $makeAppx) { throw 'Microsoft.Windows.SDK.BuildTools restore succeeded, but makeappx.exe was not found.' }

& $makeAppx.FullName pack /o /d $layoutDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

Write-Output $packagePath
