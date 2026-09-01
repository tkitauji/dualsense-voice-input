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

Console.WriteLine("PASS|Bluetooth/USB button parsing, status-cue WAV, and model integrity");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
