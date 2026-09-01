using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using DualSenseVoice;

if (args.Length != 1)
    throw new ArgumentException("Whisper model path is required.");

DualSenseBluetoothDevice device = DualSenseBluetoothCapture
    .EnumerateConnected()
    .FirstOrDefault()
    ?? throw new InvalidOperationException("Bluetooth DualSense was not found.");
string wavePath = Path.Combine(Path.GetTempPath(), $"DualSenseVoice-E2E-{Guid.NewGuid():N}.wav");

try
{
    Console.WriteLine($"CAPTURE_READY|{device.FriendlyName}");
    using var capture = DualSenseBluetoothCapture.Start(device.DevicePath, wavePath);
    await Task.Delay(TimeSpan.FromSeconds(7));
    await capture.StopAsync();
    Console.WriteLine($"CAPTURED|frames={capture.DecodedFrames}|seconds={capture.AudioDuration.TotalSeconds:F2}|energy={capture.AverageEnergy}");

    using var reader = new WaveFileReader(wavePath);
    using var wav = new MemoryStream();
    var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
    WaveFileWriter.WriteWavFileToStream(wav, resampler.ToWaveProvider16());
    wav.Position = 0;
    using var factory = WhisperFactory.FromPath(Path.GetFullPath(args[0]));
    using var processor = factory.CreateBuilder().WithLanguage("ja").Build();
    var text = new System.Text.StringBuilder();
    await foreach (var segment in processor.ProcessAsync(wav)) text.Append(segment.Text);
    Console.WriteLine($"TRANSCRIPT|{text.ToString().Trim()}");
}
finally
{
    if (File.Exists(wavePath)) File.Delete(wavePath);
}

