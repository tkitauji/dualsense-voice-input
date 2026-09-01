using System.Diagnostics;
using DualSenseVoice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

try
{
    await RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR|{exception.Message}");
    Environment.ExitCode = 1;
}

static async Task RunAsync()
{
    if (Process.GetProcessesByName("DualSenseVoice").Length > 0)
        throw new InvalidOperationException(
            "Close DualSense Voice before running the USB hardware probe.");

    IReadOnlyList<DualSenseUsbDevice> controllers =
        DualSenseMuteButtonMonitor.EnumerateConnectedUsb();
    foreach (DualSenseUsbDevice controller in controllers)
        Console.WriteLine($"USB_CONTROLLER|{controller.FriendlyName}|{controller.DevicePath}");
    DualSenseUsbDevice selectedController = controllers.FirstOrDefault()
        ?? throw new InvalidOperationException(
            "A USB-connected DualSense controller was not found. Connect a data-capable USB cable and retry.");

    using var enumerator = new MMDeviceEnumerator();
    var endpoints = new List<MMDevice>();
    foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(
                 DataFlow.Capture,
                 DeviceState.Active))
    {
        if (MainWindow.IsDualSenseAudioDevice(endpoint))
            endpoints.Add(endpoint);
        else
            endpoint.Dispose();
    }
    try
    {
        foreach (MMDevice endpoint in endpoints)
            Console.WriteLine($"USB_AUDIO_ENDPOINT|{endpoint.FriendlyName}|{endpoint.ID}");
        MMDevice selectedEndpoint = endpoints.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Windows did not expose the DualSense microphone as an active recording endpoint.");

        string wavePath = Path.Combine(
            Path.GetTempPath(),
            $"DualSenseVoice-UsbProbe-{Guid.NewGuid():N}.wav");
        try
        {
            bool? muteBefore = TryGetEndpointMute(selectedEndpoint);
            UsbCaptureResult captureResult = await CaptureBetweenButtonPressesAsync(
                selectedController,
                selectedEndpoint,
                wavePath);
            (double seconds, double averageEnergy) = MeasureWave(wavePath);

            Console.WriteLine(
                $"USB_ENDPOINT_MUTE|before={FormatNullable(muteBefore)}|afterStartPress={FormatNullable(captureResult.MuteAfterStartPress)}|afterStopPress={FormatNullable(captureResult.MuteAfterStopPress)}");
            Console.WriteLine(
                $"USB_AUDIO|bytes={captureResult.CapturedBytes}|seconds={seconds:F2}|energy={averageEnergy:F8}");
            if (captureResult.CapturedBytes == 0 ||
                seconds < 0.25 ||
                averageEnergy < 0.00000001)
                throw new InvalidOperationException(
                    "USB capture was empty or silent. Speak toward the controller between the two button presses.");

            Console.WriteLine(
                "RESULT|USB standard microphone and physical mute-button capture passed.");
        }
        finally
        {
            if (File.Exists(wavePath)) File.Delete(wavePath);
        }
    }
    finally
    {
        foreach (MMDevice endpoint in endpoints) endpoint.Dispose();
    }
}

static async Task<UsbCaptureResult> CaptureBetweenButtonPressesAsync(
    DualSenseUsbDevice controller,
    MMDevice endpoint,
    string wavePath)
{
    using var buttonMonitor = DualSenseMuteButtonMonitor.Connect(controller.DevicePath);
    Console.WriteLine("BUTTON_WAIT|Press the physical microphone button to start USB capture.");
    await WaitForButtonAsync(buttonMonitor, TimeSpan.FromSeconds(45));
    bool? muteAfterStartPress = TryGetEndpointMute(endpoint);

    long capturedBytes = 0;
    var stopped = new TaskCompletionSource<Exception?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var capture = new WasapiCapture(endpoint);
    var writer = new WaveFileWriter(wavePath, capture.WaveFormat);
    try
    {
        capture.DataAvailable += (_, eventArgs) =>
        {
            writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            capturedBytes += eventArgs.BytesRecorded;
        };
        capture.RecordingStopped += (_, eventArgs) =>
            stopped.TrySetResult(eventArgs.Exception);

        capture.StartRecording();
        Console.WriteLine(
            $"CAPTURE_STARTED|format={capture.WaveFormat}|Speak, then press the microphone button again.");
        await WaitForButtonAsync(buttonMonitor, TimeSpan.FromSeconds(45));
        bool? muteAfterStopPress = TryGetEndpointMute(endpoint);
        capture.StopRecording();

        Exception? stopError = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (stopError is not null) throw stopError;
        writer.Flush();

        return new UsbCaptureResult(
            capturedBytes,
            muteAfterStartPress,
            muteAfterStopPress);
    }
    finally
    {
        try { capture.StopRecording(); }
        catch { }
        capture.Dispose();
        writer.Dispose();
    }
}

static async Task WaitForButtonAsync(
    DualSenseMuteButtonMonitor monitor,
    TimeSpan timeout)
{
    var pressed = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler handler = (_, _) => pressed.TrySetResult();
    monitor.MuteButtonPressed += handler;
    try
    {
        await pressed.Task.WaitAsync(timeout);
    }
    finally
    {
        monitor.MuteButtonPressed -= handler;
    }
}

static (double Seconds, double AverageEnergy) MeasureWave(string wavePath)
{
    using var reader = new WaveFileReader(wavePath);
    ISampleProvider samples = reader.ToSampleProvider();
    var buffer = new float[4096];
    long sampleCount = 0;
    double energy = 0;
    int read;
    while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (int index = 0; index < read; index++)
            energy += buffer[index] * buffer[index];
        sampleCount += read;
    }

    return (
        reader.TotalTime.TotalSeconds,
        sampleCount == 0 ? 0 : energy / sampleCount);
}

static bool? TryGetEndpointMute(MMDevice endpoint)
{
    try { return endpoint.AudioEndpointVolume.Mute; }
    catch { return null; }
}

static string FormatNullable(bool? value) =>
    value.HasValue ? value.Value.ToString() : "unavailable";

internal sealed record UsbCaptureResult(
    long CapturedBytes,
    bool? MuteAfterStartPress,
    bool? MuteAfterStopPress);
