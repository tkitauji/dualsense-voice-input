#requires -Version 5.1

[CmdletBinding()]
param(
  [ValidateSet('Development', 'Submission')]
  [string]$Mode = 'Submission',

  [string]$PackagePath,

  [string]$EvidenceDirectory,

  [string]$WackReportPath,

  [string]$ScreenshotDirectory,

  [string]$HardwareChecklistPath
)

$ErrorActionPreference = 'Stop'
$checks = @()
$skips = @()

function Add-Check {
  param([string]$Name, [bool]$Passed, [string]$Detail)
  $script:checks += [pscustomobject]@{
    Name = $Name
    Passed = $Passed
    Detail = $Detail
  }
}

function Add-Skip {
  param([string]$Name, [string]$Detail)
  $script:skips += [pscustomobject]@{
    Name = $Name
    Detail = $Detail
  }
}

function Get-LatestFile {
  param([string]$Directory, [string]$Filter)
  if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
    return $null
  }
  Get-ChildItem -LiteralPath $Directory -Filter $Filter -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
}

function Test-ReportContent {
  param([string]$Directory, [string]$Filter, [string[]]$RequiredLines)
  $file = Get-LatestFile -Directory $Directory -Filter $Filter
  if ($null -eq $file) {
    return [pscustomobject]@{ Passed = $false; Detail = "No report matched $Filter" }
  }
  $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
  $missing = @($RequiredLines | Where-Object { $content -notmatch [regex]::Escape($_) })
  if ($missing.Count -gt 0) {
    return [pscustomobject]@{
      Passed = $false
      Detail = "$($file.Name) is missing: $($missing -join ', ')"
    }
  }
  [pscustomobject]@{ Passed = $true; Detail = $file.FullName }
}

function Get-PngDimensions {
  param([string]$Path)
  $stream = [System.IO.File]::OpenRead($Path)
  try {
    $header = New-Object byte[] 24
    if ($stream.Read($header, 0, $header.Length) -ne $header.Length) {
      throw 'PNG header is truncated.'
    }
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Count; $index++) {
      if ($header[$index] -ne $signature[$index]) { throw 'File is not a PNG.' }
    }
    $width = [System.Net.IPAddress]::NetworkToHostOrder(
      [System.BitConverter]::ToInt32($header, 16))
    $height = [System.Net.IPAddress]::NetworkToHostOrder(
      [System.BitConverter]::ToInt32($header, 20))
    [pscustomobject]@{ Width = $width; Height = $height }
  }
  finally { $stream.Dispose() }
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
  $defaultPackageDirectory = Join-Path $PSScriptRoot 'artifacts\package'
  $latestPackage = Get-LatestFile -Directory $defaultPackageDirectory -Filter '*.msix'
  if ($null -ne $latestPackage) {
    $PackagePath = $latestPackage.FullName
  }
}
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
  $EvidenceDirectory = Join-Path $PSScriptRoot 'artifacts\hardware'
}
if ([string]::IsNullOrWhiteSpace($ScreenshotDirectory)) {
  $ScreenshotDirectory = Join-Path $PSScriptRoot 'artifacts\screenshots'
}

$packageExists = -not [string]::IsNullOrWhiteSpace($PackagePath) -and
  (Test-Path -LiteralPath $PackagePath -PathType Leaf)
Add-Check 'MSIX package exists' $packageExists $(if ($packageExists) {
    [System.IO.Path]::GetFullPath($PackagePath)
  } else {
    'Build an MSIX or pass -PackagePath.'
  })

if ($packageExists) {
  $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = $null
  try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    $manifestEntry = $archive.Entries | Where-Object {
      $_.FullName -eq 'AppxManifest.xml'
    } | Select-Object -First 1
    Add-Check 'Package manifest exists' ($null -ne $manifestEntry) 'AppxManifest.xml'

    if ($null -ne $manifestEntry) {
      $stream = $manifestEntry.Open()
      $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
      try { [xml]$manifest = $reader.ReadToEnd() }
      finally { $reader.Dispose(); $stream.Dispose() }

      $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
      $identityName = if ($null -eq $identity) { '' } else { $identity.GetAttribute('Name') }
      $publisher = if ($null -eq $identity) { '' } else { $identity.GetAttribute('Publisher') }
      $architecture = if ($null -eq $identity) { '' } else { $identity.GetAttribute('ProcessorArchitecture') }
      $versionText = if ($null -eq $identity) { '' } else { $identity.GetAttribute('Version') }
      $publisherDisplayNode = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='PublisherDisplayName']")
      $publisherDisplayName = if ($null -eq $publisherDisplayNode) { '' } else { $publisherDisplayNode.InnerText }
      Add-Check 'Package architecture is x64' ($architecture -eq 'x64') "ProcessorArchitecture=$architecture"

      $version = $null
      $versionParsed = [Version]::TryParse($versionText, [ref]$version)
      $versionComponents = @()
      if ($versionParsed) {
        $versionComponents = @($version.Major, $version.Minor, $version.Build, $version.Revision)
      }
      $versionValid = $versionParsed -and
        $versionText -match '^\d+\.\d+\.\d+\.\d+$' -and
        @($versionComponents | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count -eq 0
      Add-Check 'Package version is four-part MSIX version' $versionValid "Version=$versionText"

      if ($Mode -eq 'Submission') {
        $storeIdentity = -not [string]::IsNullOrWhiteSpace($identityName) -and
          $identityName -ne 'DualSenseVoice.Dev' -and
          -not [string]::IsNullOrWhiteSpace($publisher) -and
          $publisher -match '^CN=' -and
          $publisher -ne 'CN=DualSenseVoiceDevelopment' -and
          -not [string]::IsNullOrWhiteSpace($publisherDisplayName) -and
          $publisherDisplayName -ne 'DualSense Voice Development'
        Add-Check 'Partner Center identity replaces development identity' $storeIdentity "Name=$identityName; Publisher=$publisher; PublisherDisplayName=$publisherDisplayName"
      } else {
        Add-Skip 'Partner Center identity' 'Development mode accepts the repository identity.'
      }

      $target = $manifest.SelectSingleNode("//*[local-name()='TargetDeviceFamily' and @Name='Windows.Desktop']")
      $minimumOs = if ($null -eq $target) { '' } else { $target.GetAttribute('MinVersion') }
      $minimumVersion = $null
      $minimumValid = [Version]::TryParse($minimumOs, [ref]$minimumVersion) -and
        $minimumVersion -ge [Version]'10.0.22000.0'
      Add-Check 'Windows 11 desktop target' $minimumValid "MinVersion=$minimumOs"

      $vclibs = $manifest.SelectSingleNode("//*[local-name()='PackageDependency' and @Name='Microsoft.VCLibs.140.00.UWPDesktop']")
      $vclibsMinimum = if ($null -eq $vclibs) { '' } else { $vclibs.GetAttribute('MinVersion') }
      $vclibsVersion = $null
      $vclibsValid = $null -ne $vclibs -and
        [Version]::TryParse($vclibsMinimum, [ref]$vclibsVersion) -and
        $vclibsVersion -ge [Version]'14.0.30704.0'
      Add-Check 'VC++ Desktop framework dependency' $vclibsValid "MinVersion=$vclibsMinimum"

      $capabilityNames = @($manifest.SelectNodes("//*[local-name()='Capability' or local-name()='DeviceCapability']") |
        ForEach-Object { $_.GetAttribute('Name') })
      Add-Check 'Microphone capability' ($capabilityNames -contains 'microphone') 'microphone'
      Add-Check 'Full-trust desktop capability' ($capabilityNames -contains 'runFullTrust') 'runFullTrust'

      $application = $manifest.SelectSingleNode("//*[local-name()='Application']")
      $applicationExecutable = if ($null -eq $application) { '' } else { $application.GetAttribute('Executable') }
      $runtimeBehavior = if ($null -eq $application) { '' } else {
        @($application.Attributes | Where-Object { $_.LocalName -eq 'RuntimeBehavior' } | Select-Object -First 1).Value
      }
      $trustLevel = if ($null -eq $application) { '' } else {
        @($application.Attributes | Where-Object { $_.LocalName -eq 'TrustLevel' } | Select-Object -First 1).Value
      }
      $desktopApplicationValid = $applicationExecutable -eq 'DualSenseVoice.exe' -and
        $runtimeBehavior -eq 'packagedClassicApp' -and
        $trustLevel -eq 'mediumIL'
      Add-Check 'Packaged desktop application entry point' $desktopApplicationValid "Executable=$applicationExecutable; RuntimeBehavior=$runtimeBehavior; TrustLevel=$trustLevel"
    }

    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\\', '/') })
    $requiredPayload = @(
      'DualSenseVoice.exe',
      'DualSenseVoice.dll',
      'Whisper.net.dll',
      'runtimes/win-x64/whisper.dll',
      'runtimes/win-x64/ggml-base-whisper.dll',
      'runtimes/win-x64/ggml-cpu-whisper.dll',
      'runtimes/win-x64/ggml-whisper.dll',
      'runtimes/noavx/win-x64/whisper.dll',
      'runtimes/noavx/win-x64/ggml-base-whisper.dll',
      'runtimes/noavx/win-x64/ggml-cpu-whisper.dll',
      'runtimes/noavx/win-x64/ggml-whisper.dll',
      'Licenses/WHISPER.NET-LICENSE.txt',
      'Licenses/WHISPER.CPP-LICENSE.txt',
      'Licenses/OPENAI-WHISPER-LICENSE.txt',
      'PRIVACY.md'
    )
    $missingPayload = @($requiredPayload | Where-Object { $entryNames -notcontains $_ })
    Add-Check 'Required runtime and notice payload' ($missingPayload.Count -eq 0) $(if ($missingPayload.Count -eq 0) {
        "$($requiredPayload.Count) required files present"
      } else {
        "Missing: $($missingPayload -join ', ')"
      })

    $unexpectedRuntime = @($entryNames | Where-Object {
      $_ -match '^runtimes/(win-x86|win-arm64|noavx/(win-x86|linux-x64))/'
    })
    Add-Check 'No incompatible native runtime payload' ($unexpectedRuntime.Count -eq 0) $(if ($unexpectedRuntime.Count -eq 0) {
        'Only x64 Whisper CPU runtimes are packaged.'
      } else {
        $unexpectedRuntime -join ', '
      })
  }
  catch {
    Add-Check 'MSIX can be inspected' $false $_.Exception.Message
  }
  finally {
    if ($null -ne $archive) { $archive.Dispose() }
  }
}

if ($Mode -eq 'Submission') {
  foreach ($scenario in @('Baseline', 'Steam', 'FF14')) {
    $required = @(
      'DUALSENSE_VOICE_COEXISTENCE_REPORT|version=2',
      "SCENARIO|$scenario",
      'RESULT|No WinMM or DirectInput interference observed while Bluetooth microphone audio was captured.'
    )
    if ($scenario -eq 'Steam') {
      $required += 'STEAM_RUNNING|True'
      $required += 'STEAM_GAME_RUNNING|True'
    }
    if ($scenario -eq 'FF14') {
      $required += 'FF14_RUNNING|True'
    }
    $reportCheck = Test-ReportContent -Directory $EvidenceDirectory -Filter "coexistence-$scenario-*.log" -RequiredLines $required
    Add-Check "Bluetooth coexistence: $scenario" $reportCheck.Passed $reportCheck.Detail
  }

  $usbCheck = Test-ReportContent -Directory $EvidenceDirectory -Filter 'usb-*.log' -RequiredLines @(
    'DUALSENSE_VOICE_USB_REPORT|version=1',
    'RESULT|USB standard microphone and physical mute-button capture passed.'
  )
  Add-Check 'USB microphone and physical button' $usbCheck.Passed $usbCheck.Detail

  if ([string]::IsNullOrWhiteSpace($HardwareChecklistPath)) {
    $latestChecklist = Get-LatestFile -Directory $EvidenceDirectory -Filter 'hardware-acceptance-*.md'
    if ($null -ne $latestChecklist) { $HardwareChecklistPath = $latestChecklist.FullName }
  }
  $checklistExists = -not [string]::IsNullOrWhiteSpace($HardwareChecklistPath) -and
    (Test-Path -LiteralPath $HardwareChecklistPath -PathType Leaf)
  if ($checklistExists) {
    $checklist = Get-Content -LiteralPath $HardwareChecklistPath -Raw -Encoding UTF8
    $completedItems = [regex]::Matches($checklist, '(?im)^- \[[xX]\] ').Count
    $openItems = [regex]::Matches($checklist, '(?im)^- \[ \] ').Count
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'HARDWARE-TEST.md') -Raw -Encoding UTF8
    $templateItems = [regex]::Matches($template, '(?im)^- \[ \] ').Count
    Add-Check 'Manual hardware acceptance checklist' ($openItems -eq 0 -and $completedItems -ge $templateItems) "completed=$completedItems; required=$templateItems; open=$openItems; path=$HardwareChecklistPath"
  } else {
    Add-Check 'Manual hardware acceptance checklist' $false 'Copy HARDWARE-TEST.md to artifacts/hardware/hardware-acceptance-<date>.md and check every item.'
  }

  if ([string]::IsNullOrWhiteSpace($WackReportPath)) {
    $latestWack = Get-LatestFile -Directory (Join-Path $PSScriptRoot 'artifacts\wack') -Filter '*.xml'
    if ($null -ne $latestWack) { $WackReportPath = $latestWack.FullName }
  }
  $wackExists = -not [string]::IsNullOrWhiteSpace($WackReportPath) -and
    (Test-Path -LiteralPath $WackReportPath -PathType Leaf)
  if ($wackExists) {
    try {
      [xml]$wack = Get-Content -LiteralPath $WackReportPath -Raw -Encoding UTF8
      $reportNode = $wack.SelectSingleNode("/*[local-name()='REPORT']")
      $overall = if ($null -eq $reportNode) { '' } else { $reportNode.GetAttribute('OVERALL_RESULT') }
      Add-Check 'WACK overall result' ($overall -eq 'PASS') "OVERALL_RESULT=$overall; path=$WackReportPath"
    }
    catch {
      Add-Check 'WACK overall result' $false $_.Exception.Message
    }
  } else {
    Add-Check 'WACK overall result' $false 'Run run-wack.ps1 against the final package.'
  }

  $screenshots = if (Test-Path -LiteralPath $ScreenshotDirectory -PathType Container) {
    @(Get-ChildItem -LiteralPath $ScreenshotDirectory -Filter '*.png' -File)
  } else { @() }
  $validScreenshots = @()
  if ($screenshots.Count -gt 0) {
    foreach ($screenshot in $screenshots) {
      try {
        $dimensions = Get-PngDimensions -Path $screenshot.FullName
        $dimensionsValid = ($dimensions.Width -ge 1366 -and $dimensions.Height -ge 768) -or
          ($dimensions.Width -ge 768 -and $dimensions.Height -ge 1366)
        if ($dimensionsValid -and $screenshot.Length -le 50MB) {
          $validScreenshots += "$($screenshot.Name)=$($dimensions.Width)x$($dimensions.Height)"
        }
      }
      catch { }
    }
  }
  Add-Check 'Store screenshot' ($validScreenshots.Count -gt 0) $(if ($validScreenshots.Count -gt 0) {
      $validScreenshots -join ', '
    } else {
      'Add at least one PNG, 1366x768 or 768x1366 minimum and no larger than 50 MB, to artifacts/screenshots.'
    })
} else {
  Add-Skip 'Hardware evidence' 'Development mode validates package structure only.'
  Add-Skip 'WACK report' 'Development mode validates package structure only.'
  Add-Skip 'Store screenshot' 'Development mode validates package structure only.'
}

foreach ($check in $checks) {
  $status = if ($check.Passed) { 'PASS' } else { 'FAIL' }
  Write-Output "$status|$($check.Name)|$($check.Detail)"
}
foreach ($skip in $skips) {
  Write-Output "SKIP|$($skip.Name)|$($skip.Detail)"
}

$failed = @($checks | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) {
  Write-Error "STORE_PREFLIGHT_FAILED|$($failed.Count) check(s) failed."
  exit 1
}

Write-Output "STORE_PREFLIGHT_PASS|mode=$Mode"
