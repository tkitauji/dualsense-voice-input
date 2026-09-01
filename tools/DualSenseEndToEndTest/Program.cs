using DualSenseVoice;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

bool buttonMode = args.Length == 2 && args[0] == "--button";
if ((!buttonMode && args.Length != 1) || (buttonMode && args.Length != 2))
    throw new ArgumentException("Usage: DualSenseEndToEndTest [--button] <Whisper model path>");

string modelPath = args[^1];
DualSenseBluetoothDevice device = DualSenseBluetoothCapture
    .EnumerateConnected()
    .FirstOrDefault()
    ?? throw new InvalidOperationException("Bluetooth DualSense was not found.");
string wavePath = Path.Combine(
    Path.GetTempPath(),
    $"DualSenseVoice-E2E-{Guid.NewGuid():N}.wav");

try
{
    Console.WriteLine($"CONTROLLER_READY|{device.FriendlyName}");
    using var capture = DualSenseBluetoothCapture.Connect(device.DevicePath);

    if (buttonMode)
    {
        Console.WriteLine("BUTTON_WAIT|press the physical microphone button to unmute");
        await WaitForMuteButtonAsync(capture, TimeSpan.FromSeconds(45));
        capture.StartRecording(wavePath);
        Console.WriteLine("BUTTON_UNMUTED|speak, then press the microphone button again");
        await WaitForMuteButtonAsync(capture, TimeSpan.FromSeconds(45));
    }
    else
    {
        capture.StartRecording(wavePath);
        Console.WriteLine("CAPTURE_STARTED|speak for 7 seconds");
        await Task.Delay(TimeSpan.FromSeconds(7));
    }

    DualSenseBluetoothRecording recording = await capture.StopRecordingAsync();
    Console.WriteLine(
        $"CAPTURED|frames={recording.DecodedFrames}|seconds={recording.AudioDuration.TotalSeconds:F2}|energy={recording.AverageEnergy}");

    if (recording.DecodedFrames == 0)
        throw new InvalidOperationException("No Bluetooth microphone frames were captured.");

    using var reader = new WaveFileReader(wavePath);
    using var wav = new MemoryStream();
    var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
    WaveFileWriter.WriteWavFileToStream(wav, resampler.ToWaveProvider16());
    wav.Position = 0;
    using var factory = WhisperFactory.FromPath(Path.GetFullPath(modelPath));
    using var processor = factory.CreateBuilder().WithLanguage("ja").Build();
    var text = new System.Text.StringBuilder();
    await foreach (var segment in processor.ProcessAsync(wav))
        text.Append(segment.Text);
    Console.WriteLine($"TRANSCRIPT|{text.ToString().Trim()}");
}
finally
{
    if (File.Exists(wavePath)) File.Delete(wavePath);
}

static async Task WaitForMuteButtonAsync(
    DualSenseBluetoothCapture capture,
    TimeSpan timeout)
{
    var pressed = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler handler = (_, _) => pressed.TrySetResult();
    capture.MuteButtonPressed += handler;
    try
    {
        await pressed.Task.WaitAsync(timeout);
    }
    finally
    {
        capture.MuteButtonPressed -= handler;
    }
}
