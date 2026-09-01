using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Concentus;
using Concentus.Structs;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace DualSenseVoice;

internal sealed record DualSenseBluetoothDevice(string DevicePath, string FriendlyName);

internal sealed class DualSenseBluetoothCapture : IDisposable
{
    private const int VendorId = 0x054C;
    private const int ProductId = 0x0CE6;
    private const int InputReportLength = 78;
    private const int MediaReportLength = 398;
    private const int OpusFrameSamples = 480;

    private readonly object audioLock = new();
    private readonly WaveFileWriter waveWriter;
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
    private readonly short[] pcm = new short[OpusFrameSamples];
    private readonly SafeFileHandle hidHandle;
    private readonly NativeHidWriter hidWriter;
    private readonly RawInputReceiver rawInput;
    private readonly CancellationTokenSource pumpCancellation = new();
    private readonly Task pumpTask;
    private byte reportSequence;
    private byte packetSequence;
    private long decodedFrames;
    private long decodedSamples;
    private long sampleEnergy;
    private int accepting = 1;
    private int stopped;
    private bool disposed;

    internal long DecodedFrames => Interlocked.Read(ref decodedFrames);
    internal TimeSpan AudioDuration => TimeSpan.FromSeconds(
        Interlocked.Read(ref decodedSamples) / 48000.0);
    internal long AverageEnergy => DecodedFrames == 0
        ? 0
        : Interlocked.Read(ref sampleEnergy) / DecodedFrames;

    private DualSenseBluetoothCapture(string devicePath, string wavePath)
    {
        waveWriter = new WaveFileWriter(wavePath, new WaveFormat(48000, 16, 1));
        hidHandle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOverlapped,
            IntPtr.Zero);
        if (hidHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            waveWriter.Dispose();
            hidHandle.Dispose();
            throw new InvalidOperationException(
                $"DualSense Bluetooth HIDを開けませんでした (Win32 {error})。Steamやコントローラー設定ソフトを閉じて再試行してください。");
        }

        try
        {
            hidWriter = new NativeHidWriter(hidHandle);
            rawInput = new RawInputReceiver(devicePath, ProcessRawReport);
            hidWriter.Write(BuildSetupReport(), 1000);
            pumpTask = Task.Run(() => PumpMedia(pumpCancellation.Token));
        }
        catch
        {
            hidHandle.Dispose();
            waveWriter.Dispose();
            throw;
        }
    }

    internal static IReadOnlyList<DualSenseBluetoothDevice> EnumerateConnected()
    {
        var results = new List<DualSenseBluetoothDevice>();
        foreach (HidDevice device in DeviceList.Local.GetHidDevices())
        {
            if (!IsDualSense(device) ||
                device.GetMaxInputReportLength() < InputReportLength ||
                device.GetMaxOutputReportLength() < MediaReportLength)
                continue;

            string product;
            try { product = device.GetProductName(); }
            catch { product = "DualSense Wireless Controller"; }
            results.Add(new DualSenseBluetoothDevice(
                device.DevicePath,
                string.IsNullOrWhiteSpace(product) ? "DualSense Wireless Controller" : product));
        }

        return results;
    }

    internal static DualSenseBluetoothCapture Start(string devicePath, string wavePath) =>
        new(devicePath, wavePath);

    private static bool IsDualSense(HidDevice device)
    {
        string path = device.DevicePath;
        return (device.VendorID == VendorId && device.ProductID == ProductId) ||
               path.Contains("pid_0ce6", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("pid&0ce6", StringComparison.OrdinalIgnoreCase);
    }

    private void PumpMedia(CancellationToken cancellationToken)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 160000;
        encoder.UseVBR = false;
        encoder.Complexity = 0;
        var silencePcm = new short[OpusFrameSamples * 2];
        var silenceOpus = new byte[200];
        int encoded = encoder.Encode(silencePcm, OpusFrameSamples, silenceOpus, silenceOpus.Length);
        if (encoded != silenceOpus.Length)
            throw new InvalidOperationException("Bluetooth音声クロック用のOpusフレームを生成できませんでした。");

        var stopwatch = Stopwatch.StartNew();
        long next = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            hidWriter.Write(
                BuildMediaReport(reportSequence, packetSequence, silenceOpus),
                1000);
            reportSequence = (byte)((reportSequence + 1) & 0x0F);
            packetSequence++;
            next += Stopwatch.Frequency * 512 / 48000;
            while (stopwatch.ElapsedTicks < next && !cancellationToken.IsCancellationRequested)
            {
                long remaining = next - stopwatch.ElapsedTicks;
                if (remaining > Stopwatch.Frequency / 500) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }
        }
    }

    private void ProcessRawReport(byte[] report)
    {
        if (Volatile.Read(ref accepting) == 0 ||
            report.Length < InputReportLength ||
            report[0] != 0x31 ||
            (report[1] & 0x02) == 0 ||
            report[3] != 0xD4)
            return;

        int payloadLength = report.Length - 3 - 4;
        try
        {
            int samples = decoder.Decode(
                report.AsSpan(3, payloadLength), pcm, OpusFrameSamples, false);
            if (samples <= 0) return;

            long energy = 0;
            for (int index = 0; index < samples; index++)
                energy += (long)pcm[index] * pcm[index];

            lock (audioLock)
            {
                if (Volatile.Read(ref accepting) == 0) return;
                waveWriter.Write(MemoryMarshal.AsBytes(pcm.AsSpan(0, samples)));
            }
            Interlocked.Increment(ref decodedFrames);
            Interlocked.Add(ref decodedSamples, samples);
            Interlocked.Add(ref sampleEnergy, energy / samples);
        }
        catch (OpusException)
        {
            // A damaged wireless frame is discarded; the next Opus frame is independent.
        }
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        Volatile.Write(ref accepting, 0);
        Exception? pumpError = null;
        pumpCancellation.Cancel();
        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pumpError = ex;
        }

        try
        {
            hidWriter.Write(
                BuildControlReport(reportSequence, packetSequence, microphoneEnabled: false),
                1000);
        }
        finally
        {
            rawInput.Dispose();
            lock (audioLock) waveWriter.Dispose();
            hidWriter.Dispose();
            hidHandle.Dispose();
            pumpCancellation.Dispose();
        }

        if (pumpError is not null)
            throw new InvalidOperationException("DualSense Bluetooth音声の送信が中断されました。", pumpError);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { }
    }

    private static byte[] BuildSetupReport()
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

    private static byte[] BuildMediaReport(
        byte sequence, byte counter, ReadOnlySpan<byte> opusSilence)
    {
        byte[] report = BuildControlReport(sequence, counter, microphoneEnabled: true);
        report[5] = report[6] = report[7] = report[8] = report[9] = 0x80;
        report[142] = 0x93;
        report[143] = 200;
        opusSilence.CopyTo(report.AsSpan(144, 200));
        WriteCrc(report);
        return report;
    }

    private static byte[] BuildControlReport(
        byte sequence, byte counter, bool microphoneEnabled)
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
        WriteCrc(report);
        return report;
    }

    private static void WriteCrc(byte[] report)
    {
        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, 0xA2);
        for (int index = 0; index < report.Length - 4; index++)
            crc = UpdateCrc(crc, report[index]);
        crc ^= 0xFFFFFFFF;
        int offset = report.Length - 4;
        report[offset] = (byte)crc;
        report[offset + 1] = (byte)(crc >> 8);
        report[offset + 2] = (byte)(crc >> 16);
        report[offset + 3] = (byte)(crc >> 24);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        return crc;
    }

    private sealed class NativeHidWriter : IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly IntPtr nativeHandle;
        private readonly EventWaitHandle completionEvent;
        private readonly IntPtr overlapped;
        private bool disposed;

        internal NativeHidWriter(SafeFileHandle handle)
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
                        throw new InvalidOperationException($"DualSenseへのWriteFileに失敗しました (Win32 {error})。 ");
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
                throw new InvalidOperationException($"DualSenseへの書込み完了を確認できませんでした (Win32 {waitError})。 ");
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

    private sealed class RawInputReceiver : IDisposable
    {
        private const int WmInput = 0x00FF;
        private const int WmClose = 0x0010;
        private const uint RidInput = 0x10000003;
        private const uint RidiDeviceName = 0x20000007;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevDeviceNotify = 0x00002000;
        private static readonly IntPtr HwndMessage = new(-3);

        private readonly string expectedDevicePath;
        private readonly Action<byte[]> reportHandler;
        private readonly Thread thread;
        private readonly ManualResetEvent started = new(false);
        private Exception? startupError;
        private IntPtr windowHandle;
        private bool disposed;

        internal RawInputReceiver(string expectedDevicePath, Action<byte[]> reportHandler)
        {
            this.expectedDevicePath = expectedDevicePath;
            this.reportHandler = reportHandler;
            thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "DualSense Bluetooth microphone Raw Input",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!started.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Raw Input受信ウィンドウを開始できませんでした。");
            if (startupError is not null)
                throw new InvalidOperationException("Raw Inputを登録できませんでした。", startupError);
        }

        private void MessageLoop()
        {
            try
            {
                using var window = new RawInputWindow(this);
                windowHandle = window.Handle;
                var registrations = new[]
                {
                    new RawInputDevice(0x01, 0x05,
                        RidevInputSink | RidevDeviceNotify, windowHandle),
                };
                if (!RawInputNative.RegisterRawInputDevices(
                        registrations,
                        (uint)registrations.Length,
                        (uint)Marshal.SizeOf<RawInputDevice>()))
                    throw new InvalidOperationException(
                        $"RegisterRawInputDevicesに失敗しました (Win32 {Marshal.GetLastWin32Error()})。 ");
                started.Set();
                Forms.Application.Run();
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
                if (header.Type != 2 || !IsExpectedDevice(header.Device)) return;
                IntPtr rawHid = IntPtr.Add(storage, (int)headerSize);
                uint reportSize = unchecked((uint)Marshal.ReadInt32(rawHid));
                uint reportCount = unchecked((uint)Marshal.ReadInt32(rawHid, 4));
                if (reportSize == 0 || reportSize > 4096 || reportCount > 128) return;

                int dataOffset = (int)headerSize + 8;
                for (uint index = 0; index < reportCount; index++)
                {
                    var report = new byte[reportSize];
                    Marshal.Copy(
                        IntPtr.Add(storage, dataOffset + checked((int)(index * reportSize))),
                        report,
                        0,
                        report.Length);
                    reportHandler(report);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }

        private bool IsExpectedDevice(IntPtr device)
        {
            uint characters = 0;
            RawInputNative.GetRawInputDeviceInfo(
                device, RidiDeviceName, null, ref characters);
            if (characters == 0) return false;
            var name = new StringBuilder((int)characters + 1);
            if (RawInputNative.GetRawInputDeviceInfo(
                    device, RidiDeviceName, name, ref characters) == uint.MaxValue)
                return false;
            return string.Equals(
                name.ToString(), expectedDevicePath, StringComparison.OrdinalIgnoreCase);
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

        private sealed class RawInputWindow : Forms.NativeWindow, IDisposable
        {
            private readonly RawInputReceiver owner;

            internal RawInputWindow(RawInputReceiver owner)
            {
                this.owner = owner;
                CreateHandle(new Forms.CreateParams
                {
                    Caption = "DualSense Voice Raw Input",
                    Parent = HwndMessage,
                });
            }

            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == WmInput)
                    owner.ReceiveRawInput(message.LParam);
                else if (message.Msg == WmClose)
                {
                    DestroyHandle();
                    Forms.Application.ExitThread();
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

            [DllImport("user32.dll", CharSet = CharSet.Unicode,
                EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
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

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const int ErrorIoPending = 997;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

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
}

