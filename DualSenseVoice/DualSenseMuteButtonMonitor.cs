using HidSharp;

namespace DualSenseVoice;

internal sealed record DualSenseUsbDevice(string DevicePath, string FriendlyName);

internal sealed class DualSenseMuteButtonMonitor : IDisposable
{
    private const int VendorId = 0x054C;
    private const int ProductId = 0x0CE6;
    private const int UsbButtons2Offset = 10;
    private const byte MicrophoneButtonMask = 0x04;

    private readonly DualSenseBluetoothCapture.RawInputReceiver rawInput;
    private bool previousPressed;

    internal event EventHandler? MuteButtonPressed;

    private DualSenseMuteButtonMonitor(string devicePath)
    {
        rawInput = new DualSenseBluetoothCapture.RawInputReceiver(
            devicePath,
            ProcessRawReport);
    }

    internal static IReadOnlyList<DualSenseUsbDevice> EnumerateConnectedUsb()
    {
        var results = new List<DualSenseUsbDevice>();
        foreach (HidDevice device in DeviceList.Local.GetHidDevices())
        {
            if (!IsDualSense(device) ||
                device.GetMaxInputReportLength() < 64 ||
                device.GetMaxOutputReportLength() >= 100)
                continue;

            string product;
            try { product = device.GetProductName(); }
            catch { product = "DualSense Wireless Controller"; }
            results.Add(new DualSenseUsbDevice(
                device.DevicePath,
                string.IsNullOrWhiteSpace(product)
                    ? "DualSense Wireless Controller"
                    : product));
        }

        return results;
    }

    internal static DualSenseMuteButtonMonitor Connect(string devicePath) => new(devicePath);

    private static bool IsDualSense(HidDevice device)
    {
        string path = device.DevicePath;
        return (device.VendorID == VendorId && device.ProductID == ProductId) ||
               path.Contains("pid_0ce6", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("pid&0ce6", StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessRawReport(byte[] report)
    {
        if (!IsMuteButtonReport(report)) return;
        bool pressed = HasMuteButtonPressed(report);
        if (pressed && !previousPressed)
            MuteButtonPressed?.Invoke(this, EventArgs.Empty);
        previousPressed = pressed;
    }

    internal static bool IsMuteButtonReport(ReadOnlySpan<byte> report) =>
        report.Length > UsbButtons2Offset && report[0] == 0x01;

    internal static bool HasMuteButtonPressed(ReadOnlySpan<byte> report) =>
        IsMuteButtonReport(report) &&
        (report[UsbButtons2Offset] & MicrophoneButtonMask) != 0;

    public void Dispose() => rawInput.Dispose();
}
