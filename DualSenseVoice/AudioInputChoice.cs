using NAudio.CoreAudioApi;

namespace DualSenseVoice;

internal enum AudioInputKind
{
    DualSenseBluetooth,
    WindowsAudio,
}

internal sealed record AudioInputChoice(
    string FriendlyName,
    AudioInputKind Kind,
    string? BluetoothDevicePath = null,
    MMDevice? WindowsDevice = null);

