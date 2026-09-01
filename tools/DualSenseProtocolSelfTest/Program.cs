using DualSenseVoice;
using NAudio.Wave;

var bluetoothReleased = new byte[78];
bluetoothReleased[0] = 0x31;
bluetoothReleased[1] = 0x00;

var bluetoothPressed = (byte[])bluetoothReleased.Clone();
bluetoothPressed[11] = 0x04;

var bluetoothAudio = (byte[])bluetoothPressed.Clone();
bluetoothAudio[1] = 0x02;
bluetoothAudio[3] = 0xD4;

var usbReleased = new byte[64];
usbReleased[0] = 0x01;

var usbPressed = (byte[])usbReleased.Clone();
usbPressed[10] = 0x04;

Assert(DualSenseBluetoothCapture.IsMuteButtonReport(bluetoothReleased),
    "Bluetooth controller report should be accepted.");
Assert(!DualSenseBluetoothCapture.HasMuteButtonPressed(bluetoothReleased),
    "Released Bluetooth button should be false.");
Assert(DualSenseBluetoothCapture.HasMuteButtonPressed(bluetoothPressed),
    "Pressed Bluetooth button should be true.");
Assert(!DualSenseBluetoothCapture.IsMuteButtonReport(bluetoothAudio),
    "Bluetooth microphone audio must not look like a button report.");

Assert(DualSenseMuteButtonMonitor.IsMuteButtonReport(usbReleased),
    "USB controller report should be accepted.");
Assert(!DualSenseMuteButtonMonitor.HasMuteButtonPressed(usbReleased),
    "Released USB button should be false.");
Assert(DualSenseMuteButtonMonitor.HasMuteButtonPressed(usbPressed),
    "Pressed USB button should be true.");
Assert(!DualSenseMuteButtonMonitor.IsMuteButtonReport(bluetoothPressed),
    "Bluetooth input must not be parsed using the USB layout.");

using MemoryStream cueStream = StatusCuePlayer.BuildWave(660, 880);
using var cueReader = new WaveFileReader(cueStream);
Assert(cueReader.WaveFormat.SampleRate == 44100,
    "Status cue must use the expected sample rate.");
Assert(cueReader.WaveFormat.Channels == 1 && cueReader.WaveFormat.BitsPerSample == 16,
    "Status cue must be 16-bit mono PCM.");
Assert(cueReader.TotalTime >= TimeSpan.FromMilliseconds(160) &&
       cueReader.TotalTime <= TimeSpan.FromMilliseconds(180),
    "Status cue duration must remain short.");

byte[] modelHash = Convert.FromHexString(MainWindow.ExpectedModelSha256);
Assert(MainWindow.IsExpectedModel(MainWindow.ExpectedModelLength, modelHash),
    "The pinned Whisper base model should be accepted.");
Assert(!MainWindow.IsExpectedModel(MainWindow.ExpectedModelLength - 1, modelHash),
    "A truncated Whisper model should be rejected.");
modelHash[0] ^= 0xFF;
Assert(!MainWindow.IsExpectedModel(MainWindow.ExpectedModelLength, modelHash),
    "A Whisper model with a different SHA-256 should be rejected.");

var intendedWindow = new IntPtr(0x101);
var ownWindow = new IntPtr(0x202);
Assert(MainWindow.IsSafePasteTarget(
        intendedWindow, ownWindow, intendedWindow, targetExists: true),
    "Paste should be allowed only after the intended target is foreground.");
Assert(!MainWindow.IsSafePasteTarget(
        intendedWindow, ownWindow, new IntPtr(0x303), targetExists: true),
    "Paste must be rejected when a different window is foreground.");
Assert(!MainWindow.IsSafePasteTarget(
        intendedWindow, ownWindow, intendedWindow, targetExists: false),
    "Paste must be rejected after the target window has closed.");
Assert(!MainWindow.IsSafePasteTarget(
        ownWindow, ownWindow, ownWindow, targetExists: true),
    "Paste must not be sent back into the app itself.");

byte[] modelPayload = Enumerable.Range(0, 4096)
    .Select(index => (byte)(index % 251))
    .ToArray();
using var modelSource = new MemoryStream(modelPayload);
using var modelDestination = new MemoryStream();
var reportedProgress = new List<int>();
long copied = await MainWindow.CopyModelWithProgressAsync(
    modelSource,
    modelDestination,
    modelPayload.Length,
    reportedProgress.Add,
    CancellationToken.None,
    bufferSize: 127);
Assert(copied == modelPayload.Length &&
       modelDestination.ToArray().SequenceEqual(modelPayload),
    "Model copy should preserve every byte.");
Assert(reportedProgress.First() == 0 && reportedProgress.Last() == 100 &&
       reportedProgress.SequenceEqual(reportedProgress.Distinct()),
    "Model copy progress should start at zero and increase to 100 without duplicates.");
using var canceledCopy = new CancellationTokenSource();
canceledCopy.Cancel();
using var canceledSource = new MemoryStream(modelPayload);
using var canceledDestination = new MemoryStream();
await AssertCanceled(async () =>
    await MainWindow.CopyModelWithProgressAsync(
        canceledSource,
        canceledDestination,
        modelPayload.Length,
        reportProgress: null,
        cancellationToken: canceledCopy.Token,
        bufferSize: 127));
Assert(MainWindow.GetModelDownloadErrorMessage(new HttpRequestException())
        .Contains("ネットワーク接続", StringComparison.Ordinal),
    "Network download errors should give a Japanese recovery instruction.");
Assert(MainWindow.GetModelDownloadErrorMessage(new IOException())
        .Contains("空き容量", StringComparison.Ordinal),
    "File download errors should give a Japanese storage instruction.");
Assert(MainWindow.GetModelDownloadErrorMessage(new InvalidDataException())
        .Contains("検証", StringComparison.Ordinal),
    "Integrity failures should give a Japanese retry instruction.");

string cleanupRoot = Path.Combine(
    Path.GetTempPath(),
    $"DualSenseVoiceSelfTest-{Guid.NewGuid():N}");
Directory.CreateDirectory(cleanupRoot);
try
{
    string interruptedRecording = Path.Combine(
        cleanupRoot,
        $"DualSenseVoice-{Guid.NewGuid():N}.wav");
    string unrelatedWave = Path.Combine(cleanupRoot, "DualSenseVoice-not-a-guid.wav");
    string modelPath = Path.Combine(cleanupRoot, "ggml-base.bin");
    string interruptedDownload = modelPath + ".download";
    File.WriteAllText(interruptedRecording, "temporary audio");
    File.WriteAllText(unrelatedWave, "must remain");
    File.WriteAllText(modelPath, "installed model");
    File.WriteAllText(interruptedDownload, "partial model");

    int cleaned = MainWindow.CleanupInterruptedFiles(cleanupRoot, modelPath);
    Assert(cleaned == 2,
        "Cleanup should remove one app recording and one partial model.");
    Assert(!File.Exists(interruptedRecording) && !File.Exists(interruptedDownload),
        "Interrupted app files should be removed.");
    Assert(File.Exists(unrelatedWave) && File.Exists(modelPath),
        "Cleanup must preserve unrelated WAV data and the installed model.");

    string lockedModel = Path.Combine(cleanupRoot, "locked-model.bin");
    File.WriteAllText(lockedModel, "locked");
    using (var lockStream = new FileStream(
               lockedModel,
               FileMode.Open,
               FileAccess.ReadWrite,
               FileShare.None))
    {
        Assert(!MainWindow.ValidateModelFile(lockedModel),
            "A locked model should be treated as invalid without crashing startup.");
    }
}
finally
{
    Directory.Delete(cleanupRoot, recursive: true);
}

Console.WriteLine(
    "PASS|Bluetooth/USB parsing, status cues, model download/recovery, paste safety, and interrupted-file cleanup");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertCanceled(Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("The canceled operation unexpectedly completed.");
    }
    catch (OperationCanceledException)
    {
    }
}
