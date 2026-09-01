using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using HidSharp;

const int VendorId = 0x054C;
const int ProductId = 0x0CE6;
const int MediaReportLength = 398;
const int InputReportLength = 78;
const int OpusFrameSamples = 480;
const int PreAudioSeconds = 3;
const int AudioSeconds = 10;

HidDevice[] devices = [];
var discoveryDeadline = DateTime.UtcNow.AddSeconds(45);
while (devices.Length == 0 && DateTime.UtcNow < discoveryDeadline)
{
    devices = FindDualSenseDevices();
    if (devices.Length == 0) Thread.Sleep(500);
}
Console.WriteLine($"Matching DualSense HID devices: {devices.Length}");
foreach (var candidate in devices)
{
    Console.WriteLine($"  {TryGetProductName(candidate)} — {candidate.DevicePath}");
    Console.WriteLine($"    input={candidate.GetMaxInputReportLength()} output={candidate.GetMaxOutputReportLength()} feature={candidate.GetMaxFeatureReportLength()}");
}

var device = devices.FirstOrDefault(d => d.GetMaxInputReportLength() >= InputReportLength && d.GetMaxOutputReportLength() >= InputReportLength)
    ?? throw new InvalidOperationException("Bluetooth DualSense HID interface was not found.");

var exclusiveCandidate = NativeMethods.CreateFile(
    device.DevicePath,
    NativeMethods.GenericRead | NativeMethods.GenericWrite,
    0,
    IntPtr.Zero,
    NativeMethods.OpenExisting,
    NativeMethods.FileFlagOverlapped,
    IntPtr.Zero);
bool openedExclusive = !exclusiveCandidate.IsInvalid;
int exclusiveOpenError = openedExclusive ? 0 : Marshal.GetLastWin32Error();
if (!openedExclusive) exclusiveCandidate.Dispose();

using var nativeReadHandle = openedExclusive ? exclusiveCandidate : NativeMethods.CreateFile(
    device.DevicePath,
    NativeMethods.GenericRead | NativeMethods.GenericWrite,
    NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
    IntPtr.Zero,
    NativeMethods.OpenExisting,
    NativeMethods.FileFlagOverlapped,
    IntPtr.Zero);
if (nativeReadHandle.IsInvalid)
    throw new InvalidOperationException($"Native HID read interface could not be opened (Win32 {Marshal.GetLastWin32Error()}).");
Console.WriteLine(openedExclusive
    ? "Opened HID interface exclusively."
    : $"Exclusive open unavailable (Win32 {exclusiveOpenError}); using shared access.");
bool inputBuffersConfigured = NativeMethods.HidD_SetNumInputBuffers(nativeReadHandle, 64);
int inputBufferError = inputBuffersConfigured ? 0 : Marshal.GetLastWin32Error();

using var nativeReader = new NativeHidReader(nativeReadHandle, InputReportLength);
using var nativeWriter = new NativeHidWriter(nativeReadHandle);
if (args.Contains("--disable", StringComparer.OrdinalIgnoreCase))
{
    uint disabled = nativeWriter.Write(BuildControlReport(0, 0, microphoneEnabled: false), 1000);
    Console.WriteLine($"Bluetooth microphone disable report accepted: {disabled} bytes");
    return;
}
using var rawInputMonitor = new DualSenseRawInputMonitor();
{
    Console.WriteLine("Opened one native Bluetooth HID interface for overlapped read/write.");
    Console.WriteLine($"Configured 64 HID input buffers: {inputBuffersConfigured}" +
                      (inputBuffersConfigured ? string.Empty : $" (Win32 {inputBufferError})"));

    var cancellation = new CancellationTokenSource();
    int reports = 0, normalReports = 0, microphoneReports = 0, decodedFrames = 0;
    long sampleEnergy = 0;

    var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
    var reader = Task.Run(() =>
    {
        var pcm = new short[OpusFrameSamples];
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                byte[]? buffer = nativeReader.ReadNext(cancellation.Token);
                if (buffer is null) break;
                int read = buffer.Length;
                Interlocked.Increment(ref reports);
                if (buffer[0] != 0x31) continue;
                byte tag = buffer[1];
                if ((tag & 0x02) == 0)
                {
                    Interlocked.Increment(ref normalReports);
                    continue;
                }

                Interlocked.Increment(ref microphoneReports);
                int payloadLength = read - 3 - 4;
                if (payloadLength <= 0 || buffer[3] != 0xD4) continue;
                int samples = decoder.Decode(buffer.AsSpan(3, payloadLength), pcm, OpusFrameSamples, false);
                if (samples <= 0) continue;
                Interlocked.Increment(ref decodedFrames);
                long energy = 0;
                for (int i = 0; i < samples; i++) energy += (long)pcm[i] * pcm[i];
                Interlocked.Add(ref sampleEnergy, energy / samples);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Read failed: {ex.Message}");
                break;
            }
        }
    });

    Console.WriteLine($"Reading ordinary gamepad reports for {PreAudioSeconds} seconds before enabling audio...");
    await Task.Delay(TimeSpan.FromSeconds(PreAudioSeconds));
    Console.WriteLine($"Pre-audio reports: {reports}");

    var setup = BuildSetupReport();
    uint setupBytes = nativeWriter.Write(setup, 1000);
    Console.WriteLine($"Setup report accepted: {setupBytes} bytes");

    var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
    encoder.Bitrate = 160000;
    encoder.UseVBR = false;
    encoder.Complexity = 0;
    var silencePcm = new short[OpusFrameSamples * 2];
    var silenceOpus = new byte[200];
    int encoded = encoder.Encode(silencePcm, OpusFrameSamples, silenceOpus, silenceOpus.Length);
    if (encoded <= 0) throw new InvalidOperationException("Could not create the Opus keep-alive frame.");
    Console.WriteLine($"Opus keep-alive bytes: {encoded}");
    Console.WriteLine($"Enabling Bluetooth microphone for {AudioSeconds} seconds. Speak into the controller now...");

    byte sequence = 0, counter = 0;
    int mediaWrites = 0;
    var stopwatch = Stopwatch.StartNew();
    long next = 0;
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(AudioSeconds))
    {
        var report = BuildMediaReport(sequence, counter, silenceOpus.AsSpan(0, encoded));
        uint written = nativeWriter.Write(report, 1000);
        if (written > 0) mediaWrites++;
        sequence = (byte)((sequence + 1) & 0x0F);
        counter++;
        next += Stopwatch.Frequency * 512 / 48000;
        while (stopwatch.ElapsedTicks < next)
        {
            long remaining = next - stopwatch.ElapsedTicks;
            if (remaining > Stopwatch.Frequency / 500) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    uint disableBytes = nativeWriter.Write(
        BuildControlReport(sequence, counter, microphoneEnabled: false), 1000);
    Console.WriteLine($"Microphone disable report accepted: {disableBytes} bytes");
    await Task.Delay(100);

    cancellation.Cancel();
    await reader.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Input reports: {reports}");
    Console.WriteLine($"Accepted media writes: {mediaWrites}");
    Console.WriteLine($"Normal gamepad reports: {normalReports}");
    Console.WriteLine($"Microphone reports: {microphoneReports}");
    Console.WriteLine($"Decoded Opus frames: {decodedFrames}");
    Console.WriteLine($"Average PCM energy: {(decodedFrames == 0 ? 0 : sampleEnergy / decodedFrames)}");
    Console.WriteLine($"Raw Input reports: {rawInputMonitor.Reports}");
    Console.WriteLine($"Raw normal gamepad reports: {rawInputMonitor.NormalReports}");
    Console.WriteLine($"Raw microphone reports: {rawInputMonitor.MicrophoneReports}");
    Console.WriteLine($"Raw decoded Opus frames: {rawInputMonitor.DecodedFrames}");
    Console.WriteLine($"Raw decoded PCM samples: {rawInputMonitor.DecodedSamples} ({rawInputMonitor.DecodedSamples / 48000.0:F2} seconds)");
    Console.WriteLine($"Raw average PCM energy: {rawInputMonitor.AverageEnergy}");
    Console.WriteLine((microphoneReports > 0 && decodedFrames > 0) || rawInputMonitor.DecodedFrames > 0
        ? "RESULT: Bluetooth microphone audio is available."
        : "RESULT: No decodable Bluetooth microphone frames were received.");
}

static byte[] BuildSetupReport()
{
    var report = new byte[InputReportLength];
    report[0] = 0x31;
    report[1] = 0x10;
    report[3] = 0xA0;
    report[4] = 0x80;
    report[8] = 0x64;
    report[10] = 0x30;
    report[40] = 0x02;
    WriteCrc(report);
    return report;
}

static byte[] BuildMediaReport(byte sequence, byte counter, ReadOnlySpan<byte> opusSilence)
{
    var report = new byte[MediaReportLength];
    report[0] = 0x36;
    report[1] = (byte)((sequence & 0x0F) << 4);
    report[2] = 0x91;
    report[3] = 7;
    report[4] = 0xFF;
    report[5] = report[6] = report[7] = report[8] = report[9] = 0x80;
    report[10] = counter;

    report[11] = 0x90;
    report[12] = 63;
    Span<byte> state = report.AsSpan(13, 63);
    state[0] = 0xFD;
    state[1] = 0xF7;
    state[4] = 0x64;
    state[5] = 0x64;
    state[6] = 0xFF;
    state[7] = 0x09;
    state[9] = 0x00;
    state[37] = 0x0A;
    state[38] = 0x07;
    state[41] = 0x02;
    state[42] = 0x01;
    state[44] = 0xFF;
    state[45] = 0xD7;

    report[76] = 0x92;
    report[77] = 64;
    report[142] = 0x93;
    report[143] = 200;
    opusSilence.CopyTo(report.AsSpan(144, Math.Min(200, opusSilence.Length)));
    WriteCrc(report);
    return report;
}

static byte[] BuildControlReport(byte sequence, byte counter, bool microphoneEnabled)
{
    var report = new byte[MediaReportLength];
    report[0] = 0x36;
    report[1] = (byte)((sequence & 0x0F) << 4);
    report[2] = 0x91;
    report[3] = 7;
    report[4] = (byte)(microphoneEnabled ? 0xFF : 0xFE);
    report[5] = report[6] = report[7] = report[8] = report[9] = 0x10;
    report[10] = counter;
    report[11] = 0x90;
    report[12] = 63;
    report[13] = 0xFD;
    report[14] = 0xF7;
    report[17] = 0x64;
    report[18] = 0x64;
    report[19] = 0xFF;
    report[20] = 0x09;
    report[50] = 0x0A;
    report[51] = 0x07;
    report[54] = 0x02;
    report[55] = 0x01;
    report[57] = 0xFF;
    report[58] = 0xD7;
    report[76] = 0x92;
    report[77] = 64;
    WriteCrc(report);
    return report;
}

static void WriteCrc(byte[] report)
{
    uint crc = 0xFFFFFFFF;
    crc = UpdateCrc(crc, 0xA2);
    for (int i = 0; i < report.Length - 4; i++) crc = UpdateCrc(crc, report[i]);
    crc ^= 0xFFFFFFFF;
    int offset = report.Length - 4;
    report[offset] = (byte)crc;
    report[offset + 1] = (byte)(crc >> 8);
    report[offset + 2] = (byte)(crc >> 16);
    report[offset + 3] = (byte)(crc >> 24);
}

static uint UpdateCrc(uint crc, byte value)
{
    crc ^= value;
    for (int bit = 0; bit < 8; bit++)
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
    return crc;
}

static string TryGetProductName(HidDevice device)
{
    try { return device.GetProductName(); }
    catch { return string.Empty; }
}

static HidDevice[] FindDualSenseDevices() => DeviceList.Local.GetHidDevices()
    .Where(d =>
    {
        string path = d.DevicePath;
        string product = TryGetProductName(d);
        return (d.VendorID == VendorId && d.ProductID == ProductId) ||
               path.Contains("pid_0ce6", StringComparison.OrdinalIgnoreCase) ||
               product.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
               product.Equals("Wireless Controller", StringComparison.OrdinalIgnoreCase);
    })
    .ToArray();

sealed class NativeHidReader : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    private readonly IntPtr nativeHandle;
    private readonly byte[] buffer;
    private readonly GCHandle pinnedBuffer;
    private readonly EventWaitHandle completionEvent;
    private readonly IntPtr overlapped;
    private bool disposed;

    internal NativeHidReader(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int reportLength)
    {
        this.handle = handle;
        nativeHandle = handle.DangerousGetHandle();
        buffer = new byte[reportLength];
        pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
    }

    internal byte[]? ReadNext(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Array.Clear(buffer);
        completionEvent.Reset();
        Marshal.StructureToPtr(new NativeOverlapped
        {
            EventHandle = completionEvent.SafeWaitHandle.DangerousGetHandle(),
        }, overlapped, false);

        bool completed = NativeMethods.ReadFile(
            nativeHandle,
            pinnedBuffer.AddrOfPinnedObject(),
            (uint)buffer.Length,
            IntPtr.Zero,
            overlapped);
        if (completed) return buffer;

        int error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ErrorIoPending)
            throw new InvalidOperationException($"ReadFile failed (Win32 {error}).");

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                var reader = (NativeHidReader)state!;
                NativeMethods.CancelIoEx(reader.nativeHandle, reader.overlapped);
            },
            this);

        if (NativeMethods.GetOverlappedResult(nativeHandle, overlapped, out uint transferred, true))
            return transferred == 0 ? null : buffer;

        error = Marshal.GetLastWin32Error();
        if (cancellationToken.IsCancellationRequested || error == NativeMethods.ErrorOperationAborted)
            return null;
        throw new InvalidOperationException($"GetOverlappedResult failed (Win32 {error}).");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NativeMethods.CancelIoEx(nativeHandle, IntPtr.Zero);
        Marshal.FreeHGlobal(overlapped);
        completionEvent.Dispose();
        pinnedBuffer.Free();
        GC.KeepAlive(handle);
    }
}

sealed class NativeHidWriter : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    private readonly IntPtr nativeHandle;
    private readonly EventWaitHandle completionEvent;
    private readonly IntPtr overlapped;
    private bool disposed;

    internal NativeHidWriter(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        this.handle = handle;
        nativeHandle = handle.DangerousGetHandle();
        completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
    }

    internal uint Write(byte[] report, uint timeoutMilliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        completionEvent.Reset();
        Marshal.StructureToPtr(new NativeOverlapped
        {
            EventHandle = completionEvent.SafeWaitHandle.DangerousGetHandle(),
        }, overlapped, false);

        GCHandle pinned = GCHandle.Alloc(report, GCHandleType.Pinned);
        try
        {
            bool completed = NativeMethods.WriteFile(
                nativeHandle,
                pinned.AddrOfPinnedObject(),
                (uint)report.Length,
                IntPtr.Zero,
                overlapped);
            if (!completed)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorIoPending)
                    throw new InvalidOperationException($"WriteFile failed (Win32 {error}).");
            }

            if (NativeMethods.GetOverlappedResultEx(
                nativeHandle,
                overlapped,
                out uint transferred,
                timeoutMilliseconds,
                false))
                return transferred;

            int waitError = Marshal.GetLastWin32Error();
            NativeMethods.CancelIoEx(nativeHandle, overlapped);
            NativeMethods.GetOverlappedResult(nativeHandle, overlapped, out _, true);
            throw new InvalidOperationException($"Write completion failed (Win32 {waitError}).");
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NativeMethods.CancelIoEx(nativeHandle, overlapped);
        Marshal.FreeHGlobal(overlapped);
        completionEvent.Dispose();
        GC.KeepAlive(handle);
    }
}

sealed class DualSenseRawInputMonitor : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmClose = 0x0010;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDeviceNotify = 0x00002000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Thread thread;
    private readonly ManualResetEvent started = new(false);
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
    private readonly short[] pcm = new short[480];
    private Exception? startupError;
    private IntPtr windowHandle;
    private long reports;
    private long normalReports;
    private long microphoneReports;
    private long decodedFrames;
    private long decodedSamples;
    private long sampleEnergy;
    private int firstReportPrinted;
    private int firstMicFramePrinted;
    private readonly HashSet<IntPtr> announcedDevices = [];
    private bool disposed;

    internal long Reports => Interlocked.Read(ref reports);
    internal long NormalReports => Interlocked.Read(ref normalReports);
    internal long MicrophoneReports => Interlocked.Read(ref microphoneReports);
    internal long DecodedFrames => Interlocked.Read(ref decodedFrames);
    internal long DecodedSamples => Interlocked.Read(ref decodedSamples);
    internal long AverageEnergy => DecodedFrames == 0 ? 0 : Interlocked.Read(ref sampleEnergy) / DecodedFrames;

    internal DualSenseRawInputMonitor()
    {
        thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "DualSense Raw Input monitor",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!started.WaitOne(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Raw Input window did not start.");
        if (startupError is not null)
            throw new InvalidOperationException("Raw Input registration failed.", startupError);
    }

    private void MessageLoop()
    {
        try
        {
            using var window = new RawInputWindow(this);
            windowHandle = window.Handle;
            var registrations = new[]
            {
                new RawInputDevice(0x01, 0x05, RidevInputSink | RidevDeviceNotify, windowHandle),
            };
            if (!RawInputNative.RegisterRawInputDevices(
                    registrations,
                    (uint)registrations.Length,
                    (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new InvalidOperationException($"RegisterRawInputDevices failed (Win32 {Marshal.GetLastWin32Error()}).");
            Console.WriteLine("Registered a Windows Raw Input gamepad monitor.");
            started.Set();
            Application.Run();
        }
        catch (Exception ex)
        {
            startupError = ex;
            started.Set();
        }
        finally
        {
            windowHandle = IntPtr.Zero;
        }
    }

    private void ReceiveRawInput(IntPtr rawInputHandle)
    {
        uint byteCount = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (RawInputNative.GetRawInputData(
                rawInputHandle, RidInput, IntPtr.Zero, ref byteCount, headerSize) == uint.MaxValue ||
            byteCount < headerSize + 8)
            return;

        IntPtr storage = Marshal.AllocHGlobal((int)byteCount);
        try
        {
            uint copied = RawInputNative.GetRawInputData(
                rawInputHandle, RidInput, storage, ref byteCount, headerSize);
            if (copied == uint.MaxValue || copied < headerSize + 8) return;

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(storage);
            if (header.Type != 2 || !IsDualSense(header.Device)) return;
            IntPtr rawHid = IntPtr.Add(storage, (int)headerSize);
            uint reportSize = unchecked((uint)Marshal.ReadInt32(rawHid));
            uint reportCount = unchecked((uint)Marshal.ReadInt32(rawHid, 4));
            if (reportSize == 0 || reportSize > 4096 || reportCount > 128) return;

            int dataOffset = (int)headerSize + 8;
            for (uint index = 0; index < reportCount; index++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(IntPtr.Add(storage, dataOffset + checked((int)(index * reportSize))), report, 0, report.Length);
                ProcessReport(report);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(storage);
        }
    }

    private void ProcessReport(byte[] report)
    {
        Interlocked.Increment(ref reports);
        if (Interlocked.Exchange(ref firstReportPrinted, 1) == 0)
            Console.WriteLine($"First Raw Input report ({report.Length} bytes): {Convert.ToHexString(report.AsSpan(0, Math.Min(16, report.Length)))}");
        if (report.Length < 2 || report[0] != 0x31) return;
        if ((report[1] & 0x02) == 0)
        {
            Interlocked.Increment(ref normalReports);
            return;
        }

        Interlocked.Increment(ref microphoneReports);
        int payloadLength = report.Length - 3 - 4;
        if (payloadLength <= 0 || report[3] != 0xD4) return;
        try
        {
            int samples = decoder.Decode(report.AsSpan(3, payloadLength), pcm, pcm.Length, false);
            if (samples <= 0) return;
            Interlocked.Increment(ref decodedFrames);
            Interlocked.Add(ref decodedSamples, samples);
            if (Interlocked.Exchange(ref firstMicFramePrinted, 1) == 0)
                Console.WriteLine($"First microphone Opus frame decoded to {samples} PCM samples.");
            long energy = 0;
            for (int i = 0; i < samples; i++) energy += (long)pcm[i] * pcm[i];
            Interlocked.Add(ref sampleEnergy, energy / samples);
        }
        catch (OpusException)
        {
        }
    }

    private bool IsDualSense(IntPtr device)
    {
        uint characters = 0;
        RawInputNative.GetRawInputDeviceInfo(device, RidiDeviceName, null, ref characters);
        if (characters == 0) return false;
        var name = new StringBuilder((int)characters + 1);
        if (RawInputNative.GetRawInputDeviceInfo(device, RidiDeviceName, name, ref characters) == uint.MaxValue)
            return false;
        string path = name.ToString();
        bool dualSense = path.Contains("pid_0ce6", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("pid&0ce6", StringComparison.OrdinalIgnoreCase);
        if (dualSense && announcedDevices.Add(device))
            Console.WriteLine($"Raw Input DualSense source: {path}");
        return dualSense;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IntPtr handle = windowHandle;
        if (handle != IntPtr.Zero)
            RawInputNative.PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputDevice
    {
        internal readonly ushort UsagePage;
        internal readonly ushort Usage;
        internal readonly uint Flags;
        internal readonly IntPtr Target;

        internal RawInputDevice(ushort usagePage, ushort usage, uint flags, IntPtr target)
        {
            UsagePage = usagePage;
            Usage = usage;
            Flags = flags;
            Target = target;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputHeader
    {
        internal readonly uint Type;
        internal readonly uint Size;
        internal readonly IntPtr Device;
        internal readonly IntPtr WParam;
    }

    private sealed class RawInputWindow : NativeWindow, IDisposable
    {
        private readonly DualSenseRawInputMonitor owner;

        internal RawInputWindow(DualSenseRawInputMonitor owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "DualSense Raw Input Monitor",
                Parent = HwndMessage,
            });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmInput)
                owner.ReceiveRawInput(message.LParam);
            else if (message.Msg == WmClose)
            {
                DestroyHandle();
                Application.ExitThread();
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    private static class RawInputNative
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterRawInputDevices(
            [In] RawInputDevice[] devices,
            uint numberDevices,
            uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(
            IntPtr rawInput,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            StringBuilder? data,
            ref uint size);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }
}

static class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const int ErrorIoPending = 997;
    internal const int ErrorOperationAborted = 995;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_SetNumInputBuffers(
        Microsoft.Win32.SafeHandles.SafeFileHandle hidDeviceObject,
        int numberBuffers);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(
        IntPtr file,
        IntPtr buffer,
        uint numberOfBytesToRead,
        IntPtr numberOfBytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        IntPtr file,
        IntPtr buffer,
        uint numberOfBytesToWrite,
        IntPtr numberOfBytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResult(
        IntPtr file,
        IntPtr overlapped,
        out uint numberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResultEx(
        IntPtr file,
        IntPtr overlapped,
        out uint numberOfBytesTransferred,
        uint milliseconds,
        [MarshalAs(UnmanagedType.Bool)] bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
}

