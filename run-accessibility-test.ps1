param(
  [Parameter(Mandatory = $true)]
  [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$probeAssemblies = @(
  ([System.Windows.Automation.Automation]).Assembly.Location,
  ([System.Windows.Automation.AutomationEventHandler]).Assembly.Location
)
Add-Type -TypeDefinition @"
using System.Windows.Automation;

public static class DualSenseVoiceLiveRegionProbe
{
    public static readonly AutomationEventHandler Handler = OnEvent;
    public static volatile bool Received;
    public static string AutomationId = "";
    public static string Name = "";

    private static void OnEvent(object sender, AutomationEventArgs args)
    {
        AutomationElement element = sender as AutomationElement;
        if (element == null)
            return;

        AutomationId = element.Current.AutomationId;
        Name = element.Current.Name;
        Received = true;
    }
}
"@ -ReferencedAssemblies $probeAssemblies

function Copy-ClipboardDataObject {
  $source = [System.Windows.Clipboard]::GetDataObject()
  if ($null -eq $source) {
    return $null
  }

  $snapshot = New-Object System.Windows.DataObject
  foreach ($format in $source.GetFormats($false)) {
    $data = $source.GetData($format, $false)
    if ($null -ne $data) {
      $snapshot.SetData($format, $data)
    }
  }
  return $snapshot
}

$originalClipboard = Copy-ClipboardDataObject
$process = Start-Process -FilePath $ExecutablePath -WindowStyle Hidden -PassThru
$root = $null
$eventRegistered = $false

try {
  for ($attempt = 0; $attempt -lt 50; $attempt++) {
    $process.Refresh()
    if ($process.HasExited) {
      throw "The app exited before UI Automation verification. Exit code: $($process.ExitCode)"
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
      $root = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
      break
    }
    Start-Sleep -Milliseconds 100
  }

  if ($null -eq $root) {
    throw 'The app main window was not found.'
  }

  $condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    'RefreshButton')
  $button = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $condition)
  if ($null -eq $button) {
    throw 'RefreshButton was not found in the UI Automation tree.'
  }

  [System.Windows.Automation.Automation]::AddAutomationEventHandler(
    [System.Windows.Automation.AutomationElementIdentifiers]::LiveRegionChangedEvent,
    $root,
    [System.Windows.Automation.TreeScope]::Descendants,
    [DualSenseVoiceLiveRegionProbe]::Handler)
  $eventRegistered = $true

  $invoke = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
  $invoke.Invoke()

  for ($attempt = 0; $attempt -lt 50 -and -not [DualSenseVoiceLiveRegionProbe]::Received; $attempt++) {
    Start-Sleep -Milliseconds 100
  }
  if (-not [DualSenseVoiceLiveRegionProbe]::Received) {
    throw 'No LiveRegionChanged event was received after the status update.'
  }

  if ([DualSenseVoiceLiveRegionProbe]::AutomationId -ne 'StatusText') {
    throw "Unexpected LiveRegion source: $([DualSenseVoiceLiveRegionProbe]::AutomationId)"
  }

  Write-Output "LIVE_REGION_PASS|$([DualSenseVoiceLiveRegionProbe]::AutomationId)|$([DualSenseVoiceLiveRegionProbe]::Name)"

  $transcriptCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    'TranscriptBox')
  $transcript = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $transcriptCondition)
  $copyCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    'CopyButton')
  $copyButton = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $copyCondition)
  if ($null -eq $transcript -or $null -eq $copyButton) {
    throw 'TranscriptBox or CopyButton was not found in the UI Automation tree.'
  }

  $probeText = 'dualsense-clipboard-probe'
  $value = [System.Windows.Automation.ValuePattern]$transcript.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern)
  $value.SetValue($probeText)
  $copyInvoke = [System.Windows.Automation.InvokePattern]$copyButton.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
  $copyInvoke.Invoke()

  $clipboardText = ''
  for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 50
    try {
      $clipboardText = [System.Windows.Clipboard]::GetText()
      if ($clipboardText -eq $probeText) {
        break
      }
    }
    catch [System.Runtime.InteropServices.ExternalException] {
    }
  }
  if ($clipboardText -ne $probeText) {
    throw 'The Copy button did not place the transcript on the clipboard.'
  }
  Write-Output "CLIPBOARD_COPY_PASS|$clipboardText"
}
finally {
  if ($eventRegistered) {
    [System.Windows.Automation.Automation]::RemoveAutomationEventHandler(
      [System.Windows.Automation.AutomationElementIdentifiers]::LiveRegionChangedEvent,
      $root,
      [DualSenseVoiceLiveRegionProbe]::Handler)
  }
  if (-not $process.HasExited) {
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(3000)) {
      $process.Kill()
      $process.WaitForExit()
    }
  }
  $process.Dispose()

  if ($null -eq $originalClipboard) {
    [System.Windows.Clipboard]::Clear()
  }
  else {
    [System.Windows.Clipboard]::SetDataObject($originalClipboard, $true)
  }
}
