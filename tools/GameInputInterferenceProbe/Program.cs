using System.Runtime.InteropServices;
using System.Diagnostics;
using DualSenseVoice;
using SharpGen.Runtime;
using Vortice.DirectInput;
using static Vortice.DirectInput.DInput;

try
{
    if (Process.GetProcessesByName("DualSenseVoice").Length > 0)
        throw new InvalidOperationException(
            "Close DualSense Voice before running the interference probe; two microphone clocks must not write to one controller.");

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
        throw new InvalidOperationException("WinMM did not expose the DualSense.");
    uint joystickId = selected.Id;

    using IDirectInput8 directInput = DirectInput8Create();
    IList<DeviceInstance> directInputDevices = directInput.GetDevices(
        DeviceClass.GameControl,
        DeviceEnumerationFlags.AttachedOnly);
    foreach (DeviceInstance candidate in directInputDevices)
    {
        Console.WriteLine(
            $"DIRECTINPUT_DEVICE|instance={candidate.InstanceName}|product={candidate.ProductName}|guid={candidate.InstanceGuid}");
    }

    DeviceInstance? selectedDirectInput = directInputDevices.FirstOrDefault(candidate =>
        candidate.InstanceName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
        candidate.InstanceName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
        candidate.ProductName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
        candidate.ProductName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase));
    if (selectedDirectInput is null && directInputDevices.Count == 1)
        selectedDirectInput = directInputDevices[0];
    if (selectedDirectInput is null)
        throw new InvalidOperationException("DirectInput did not expose the DualSense.");

    using IDirectInputDevice8 directInputDevice = directInput.CreateDevice(
        selectedDirectInput.InstanceGuid);
    directInputDevice.SetDataFormat<RawJoystickState>().CheckError();
    directInputDevice.SetCooperativeLevel(
        IntPtr.Zero,
        CooperativeLevel.Background | CooperativeLevel.NonExclusive).CheckError();
    directInputDevice.Acquire().CheckError();

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
        ReadingPair baseline = await SampleBothAsync(
            joystickId,
            directInputDevice,
            TimeSpan.FromSeconds(2),
            null);
        if (!IsUsableBaseline(baseline.WinMm) ||
            !IsUsableBaseline(baseline.DirectInput))
        {
            throw new InvalidOperationException(
                "Baseline game input was unreadable or a button/D-pad was active. Keep the controller neutral and run the probe again.");
        }

        using var capture = DualSenseBluetoothCapture.Connect(device.DevicePath);
        capture.StartRecording(wavePath);
        Console.WriteLine("MIC_ON|Keep the controller neutral for 5 seconds.");
        ReadingPair microphone = await SampleBothAsync(
            joystickId,
            directInputDevice,
            TimeSpan.FromSeconds(5),
            baseline);
        DualSenseBluetoothRecording recording = await capture.StopRecordingAsync();

        Console.WriteLine(
            $"AUDIO|frames={recording.DecodedFrames}|seconds={recording.AudioDuration.TotalSeconds:F2}");
        Console.WriteLine(
            $"WINMM|maxAxisDelta={microphone.WinMm.MaxAxisDelta:F4}|largeAxisSamples={microphone.WinMm.LargeAxisSamples}/{microphone.WinMm.SampleCount}|activeControlSamples={microphone.WinMm.ActiveControlSamples}/{microphone.WinMm.SampleCount}|readErrors={microphone.WinMm.ReadErrors}");
        Console.WriteLine(
            $"DIRECTINPUT|maxAxisDelta={microphone.DirectInput.MaxAxisDelta:F4}|largeAxisSamples={microphone.DirectInput.LargeAxisSamples}/{microphone.DirectInput.SampleCount}|activeControlSamples={microphone.DirectInput.ActiveControlSamples}/{microphone.DirectInput.SampleCount}|readErrors={microphone.DirectInput.ReadErrors}");

        bool noInterference = IsClean(microphone.WinMm) &&
            IsClean(microphone.DirectInput) &&
            recording.DecodedFrames > 0;
        Console.WriteLine(noInterference
            ? "RESULT|No WinMM or DirectInput interference observed while Bluetooth microphone audio was captured."
            : "RESULT|FAILED: microphone capture changed game input, caused read errors, or produced no audio.");
        if (!noInterference)
            Environment.ExitCode = 2;
    }
    finally
    {
        if (File.Exists(wavePath)) File.Delete(wavePath);
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR|{exception.Message}");
    Environment.ExitCode = 1;
}

static bool IsClean(Reading reading) =>
    reading.SampleCount > 0 &&
    reading.MaxAxisDelta < 0.10 &&
    reading.LargeAxisSamples == 0 &&
    reading.ActiveControlSamples == 0 &&
    reading.ReadErrors == 0;

static bool IsUsableBaseline(Reading reading) =>
    reading.SampleCount > 0 &&
    reading.ActiveControlSamples == 0 &&
    reading.ReadErrors == 0;

static async Task<ReadingPair> SampleBothAsync(
    uint joystickId,
    IDirectInputDevice8 directInputDevice,
    TimeSpan duration,
    ReadingPair? reference)
{
    Task<Reading> winMm = SampleWinMmAsync(
        joystickId,
        duration,
        reference?.WinMm.AxisAverage);
    Task<Reading> directInput = SampleDirectInputAsync(
        directInputDevice,
        duration,
        reference?.DirectInput.AxisAverage);
    await Task.WhenAll(winMm, directInput);
    return new ReadingPair(await winMm, await directInput);
}

static async Task<Reading> SampleWinMmAsync(
    uint joystickId,
    TimeSpan duration,
    double[]? axisReference)
{
    var axisTotal = new double[6];
    int sampleCount = 0;
    int largeAxisSamples = 0;
    int activeControlSamples = 0;
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
            if (axisReference is null) continue;
            double delta = Math.Abs(axes[index] - axisReference[index]);
            maxAxisDelta = Math.Max(maxAxisDelta, delta);
            if (delta >= 0.25)
            {
                largeAxisSamples++;
                break;
            }
        }
        if (info.Buttons != 0 || info.Pov != ushort.MaxValue)
            activeControlSamples++;
        await Task.Delay(5);
    }

    for (int index = 0; index < axisTotal.Length; index++)
        axisTotal[index] /= Math.Max(sampleCount, 1);
    return new Reading(
        axisTotal,
        sampleCount,
        maxAxisDelta,
        largeAxisSamples,
        activeControlSamples,
        readErrors);
}

static async Task<Reading> SampleDirectInputAsync(
    IDirectInputDevice8 device,
    TimeSpan duration,
    double[]? axisReference)
{
    var axisTotal = new double[8];
    int sampleCount = 0;
    int largeAxisSamples = 0;
    int activeControlSamples = 0;
    int readErrors = 0;
    double maxAxisDelta = 0;
    DateTime deadline = DateTime.UtcNow + duration;

    while (DateTime.UtcNow < deadline)
    {
        try
        {
            if (device.Poll().Failure)
            {
                device.Acquire();
                device.Poll().CheckError();
            }

            JoystickState state = device.GetCurrentJoystickState();
            double[] axes =
            [
                NormalizeDirectInputAxis(state.X),
                NormalizeDirectInputAxis(state.Y),
                NormalizeDirectInputAxis(state.Z),
                NormalizeDirectInputAxis(state.RotationX),
                NormalizeDirectInputAxis(state.RotationY),
                NormalizeDirectInputAxis(state.RotationZ),
                NormalizeDirectInputAxis(state.Sliders[0]),
                NormalizeDirectInputAxis(state.Sliders[1]),
            ];
            sampleCount++;
            for (int index = 0; index < axes.Length; index++)
            {
                axisTotal[index] += axes[index];
                if (axisReference is null) continue;
                double delta = Math.Abs(axes[index] - axisReference[index]);
                maxAxisDelta = Math.Max(maxAxisDelta, delta);
                if (delta >= 0.25)
                {
                    largeAxisSamples++;
                    break;
                }
            }

            bool buttonPressed = state.Buttons.Any(pressed => pressed);
            bool pointOfViewActive = state.PointOfViewControllers.Any(value => value >= 0);
            if (buttonPressed || pointOfViewActive)
                activeControlSamples++;
        }
        catch (SharpGenException)
        {
            readErrors++;
            device.Acquire();
        }

        await Task.Delay(5);
    }

    for (int index = 0; index < axisTotal.Length; index++)
        axisTotal[index] /= Math.Max(sampleCount, 1);
    return new Reading(
        axisTotal,
        sampleCount,
        maxAxisDelta,
        largeAxisSamples,
        activeControlSamples,
        readErrors);
}

static double NormalizeDirectInputAxis(int value) =>
    Math.Clamp(value / 65535.0, 0.0, 1.0);

internal sealed record ReadingPair(Reading WinMm, Reading DirectInput);

internal sealed record Reading(
    double[] AxisAverage,
    int SampleCount,
    double MaxAxisDelta,
    int LargeAxisSamples,
    int ActiveControlSamples,
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
