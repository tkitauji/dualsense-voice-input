using System.IO;
using System.Media;

namespace DualSenseVoice;

internal static class StatusCuePlayer
{
    private const int SampleRate = 44100;
    private const int ToneMilliseconds = 85;
    private const int FadeMilliseconds = 8;
    private static readonly SemaphoreSlim PlaybackLock = new(1, 1);

    internal static void PlayStarted() => Play(660, 880);

    internal static void PlayStopped() => Play(880, 660);

    private static void Play(double firstFrequency, double secondFrequency) =>
        _ = Task.Run(async () =>
        {
            await PlaybackLock.WaitAsync();
            try
            {
                using MemoryStream wave = BuildWave(firstFrequency, secondFrequency);
                using var player = new SoundPlayer(wave);
                player.PlaySync();
            }
            catch
            {
                // Audio feedback is helpful but must never break voice input.
            }
            finally
            {
                PlaybackLock.Release();
            }
        });

    internal static MemoryStream BuildWave(double firstFrequency, double secondFrequency)
    {
        int samplesPerTone = SampleRate * ToneMilliseconds / 1000;
        int totalSamples = samplesPerTone * 2;
        var stream = new MemoryStream(44 + totalSamples * sizeof(short));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + totalSamples * sizeof(short));
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(totalSamples * sizeof(short));

            WriteTone(writer, firstFrequency, samplesPerTone);
            WriteTone(writer, secondFrequency, samplesPerTone);
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteTone(BinaryWriter writer, double frequency, int sampleCount)
    {
        int fadeSamples = SampleRate * FadeMilliseconds / 1000;
        for (int index = 0; index < sampleCount; index++)
        {
            double envelope = 1;
            if (index < fadeSamples)
                envelope = index / (double)fadeSamples;
            else if (index >= sampleCount - fadeSamples)
                envelope = (sampleCount - index - 1) / (double)fadeSamples;

            double phase = 2 * Math.PI * frequency * index / SampleRate;
            writer.Write((short)(Math.Sin(phase) * envelope * short.MaxValue * 0.16));
        }
    }
}
