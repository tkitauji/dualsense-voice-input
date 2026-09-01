using System.Runtime.InteropServices;
using DualSenseVoice;

var joysticks = new List<(uint Id, string Name)>();
for (uint id = 0; id < NativeMethods.joyGetNumDevs(); id++)
{
    var info = JoyInfoEx.Create();
    if (NativeMethods.joyGetPosEx(id, ref info) != 0) continue;
    var caps = new JoyCaps();
    if (NativeMethods.joyGetDevCaps(id, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0)
        continue;
    joysticks.Add((id, caps.ProductName));
    Console.WriteLine(
        $"JOYSTICK|id={id}|{caps.ProductName}|mid={caps.ManufacturerId:X4}|pid={caps.ProductId:X4}|axes={caps.AxisCount}|buttons={caps.ButtonCount}");
}

var selected = joysticks.FirstOrDefault(item =>
    item.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
    item.Name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase));
if (string.IsNullOrEmpty(selected.Name) && joysticks.Count == 1)
    selected = joysticks[0];
if (string.IsNullOrEmpty(selected.Name))
    throw new InvalidOperationException("WinMM/DirectInput did not expose the DualSense.");
uint joystickId = selected.Id;

DualSenseBluetoothDevice device = DualSenseBluetoothCapture
    .EnumerateConnected()
    .FirstOrDefault()
    ?? throw new InvalidOperationException("Bluetooth DualSense HID was not found.");
string wavePath = Path.Combine(
    Path.GetTempPath(),
    $"DualSenseVoice-Interference-{Guid.NewGuid():N}.wav");

try
{
    Console.WriteLine("BASELINE|Keep the controller neutral for 2 seconds.");
    Reading baseline = await SampleAsync(joystickId, TimeSpan.FromSeconds(2), null);

    using var capture = DualSenseBluetoothCapture.Connect(device.DevicePath);
    capture.StartRecording(wavePath);
    Console.WriteLine("MIC_ON|Keep the controller neutral for 5 seconds.");
    Reading microphone = await SampleAsync(
        joystickId,
        TimeSpan.FromSeconds(5),
        baseline.AxisAverage);
    DualSenseBluetoothRecording recording = await capture.StopRecordingAsync();

    Console.WriteLine(
        $"AUDIO|frames={recording.DecodedFrames}|seconds={recording.AudioDuration.TotalSeconds:F2}");
    Console.WriteLine(
        $"GAME_INPUT|maxAxisDelta={microphone.MaxAxisDelta:F4}|largeAxisSamples={microphone.LargeAxisSamples}/{microphone.SampleCount}|buttonSamples={microphone.ButtonSamples}/{microphone.SampleCount}|readErrors={microphone.ReadErrors}");
    Console.WriteLine(
        microphone.MaxAxisDelta < 0.10 &&
        microphone.LargeAxisSamples == 0 &&
        microphone.ButtonSamples == 0 &&
        microphone.ReadErrors == 0
            ? "RESULT|No WinMM/DirectInput interference observed."
            : "RESULT|Microphone reports interfere with WinMM/DirectInput state.");
}
finally
{
    if (File.Exists(wavePath)) File.Delete(wavePath);
}

static async Task<Reading> SampleAsync(
    uint joystickId,
    TimeSpan duration,
    double[]? reference)
{
    var axisTotal = new double[6];
    int sampleCount = 0;
    int largeAxisSamples = 0;
    int buttonSamples = 0;
    int readErrors = 0;
    double maxAxisDelta = 0;
    DateTime deadline = DateTime.UtcNow + duration;

    while (DateTime.UtcNow < deadline)
    {
        var info = JoyInfoEx.Create();
        if (NativeMethods.joyGetPosEx(joystickId, ref info) != 0)
        {
            readErrors++;
            await Task.Delay(5);
            continue;
        }

        double[] axes =
        [
            info.X / 65535.0,
            info.Y / 65535.0,
            info.Z / 65535.0,
            info.R / 65535.0,
            info.U / 65535.0,
            info.V / 65535.0,
        ];
        sampleCount++;
        for (int index = 0; index < axes.Length; index++)
        {
            axisTotal[index] += axes[index];
            if (reference is null) continue;
            double delta = Math.Abs(axes[index] - reference[index]);
            maxAxisDelta = Math.Max(maxAxisDelta, delta);
            if (delta >= 0.25)
            {
                largeAxisSamples++;
                break;
            }
        }
        if (info.Buttons != 0) buttonSamples++;
        await Task.Delay(5);
    }

    for (int index = 0; index < axisTotal.Length; index++)
        axisTotal[index] /= Math.Max(sampleCount, 1);
    return new Reading(
        axisTotal,
        sampleCount,
        maxAxisDelta,
        largeAxisSamples,
        buttonSamples,
        readErrors);
}

internal sealed record Reading(
    double[] AxisAverage,
    int SampleCount,
    double MaxAxisDelta,
    int LargeAxisSamples,
    int ButtonSamples,
    int ReadErrors);

[StructLayout(LayoutKind.Sequential)]
internal struct JoyInfoEx
{
    internal uint Size;
    internal uint Flags;
    internal uint X;
    internal uint Y;
    internal uint Z;
    internal uint R;
    internal uint U;
    internal uint V;
    internal uint Buttons;
    internal uint ButtonNumber;
    internal uint Pov;
    internal uint Reserved1;
    internal uint Reserved2;

    internal static JoyInfoEx Create() => new()
    {
        Size = (uint)Marshal.SizeOf<JoyInfoEx>(),
        Flags = 0x000000FF,
    };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct JoyCaps
{
    internal ushort ManufacturerId;
    internal ushort ProductId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string ProductName;
    internal uint XMin;
    internal uint XMax;
    internal uint YMin;
    internal uint YMax;
    internal uint ZMin;
    internal uint ZMax;
    internal uint ButtonCount;
    internal uint PeriodMin;
    internal uint PeriodMax;
    internal uint RMin;
    internal uint RMax;
    internal uint UMin;
    internal uint UMax;
    internal uint VMin;
    internal uint VMax;
    internal uint Capabilities;
    internal uint MaxAxes;
    internal uint AxisCount;
    internal uint MaxButtons;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string RegistryKey;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string OemVxd;
}

internal static partial class NativeMethods
{
    [DllImport("winmm.dll")]
    internal static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    internal static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern uint joyGetDevCaps(
        uint joystickId,
        ref JoyCaps capabilities,
        uint capabilitiesSize);
}
