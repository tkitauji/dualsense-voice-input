using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace DualSenseVoice;

public partial class MainWindow : Window
{
    static readonly TimeSpan MaximumRecordingTime = TimeSpan.FromSeconds(60);

    readonly MMDeviceEnumerator audioDevices = new();
    readonly DispatcherTimer recordingTimer = new();
    readonly DispatcherTimer connectionTimer = new();
    DualSenseBluetoothCapture? bluetoothCapture;
    DualSenseMuteButtonMonitor? usbButtonMonitor;
    WasapiCapture? windowsCapture;
    WaveFileWriter? windowsWriter;
    TaskCompletionSource? windowsCaptureStopped;
    string? recordingPath;
    IntPtr previousWindow;
    bool busy;
    bool suppressDeviceSelection;

    bool IsRecording =>
        bluetoothCapture?.IsRecording == true || windowsCapture is not null;

    string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseVoice",
        "ggml-base.bin");

    bool HasValidModel =>
        File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 100_000_000;

    public MainWindow()
    {
        InitializeComponent();
        recordingTimer.Interval = MaximumRecordingTime;
        recordingTimer.Tick += RecordingTimer_Tick;
        connectionTimer.Interval = TimeSpan.FromSeconds(3);
        connectionTimer.Tick += ConnectionTimer_Tick;
        Loaded += (_, _) =>
        {
            UpdateModelState();
            RefreshDevices();
            connectionTimer.Start();
        };
        Closed += (_, _) =>
        {
            recordingTimer.Stop();
            connectionTimer.Stop();
            StopWindowsCaptureImmediately();
            DisconnectInput();
            DisposeDeviceChoices();
            DeleteRecordingFile();
            audioDevices.Dispose();
        };
    }

    void RefreshDevices()
    {
        if (busy || IsRecording) return;

        string? selectedKey = (DeviceBox.SelectedItem as AudioInputChoice)?.ConnectionKey;
        DisconnectInput();
        suppressDeviceSelection = true;
        DisposeDeviceChoices();

        try
        {
            var choices = new List<AudioInputChoice>();
            DualSenseUsbDevice? usbController =
                DualSenseMuteButtonMonitor.EnumerateConnectedUsb().FirstOrDefault();

            if (usbController is not null)
            {
                foreach (MMDevice device in audioDevices.EnumerateAudioEndPoints(
                             DataFlow.Capture,
                             DeviceState.Active))
                {
                    if (!IsDualSenseAudioDevice(device)) continue;
                    choices.Add(new AudioInputChoice(
                        $"{device.FriendlyName} — USB（Windows標準）",
                        AudioInputKind.WindowsAudio,
                        WindowsDevice: device,
                        ButtonDevicePath: usbController.DevicePath));
                }
            }

            choices.AddRange(DualSenseBluetoothCapture.EnumerateConnected()
                .Select(device => new AudioInputChoice(
                    $"{device.FriendlyName} — Bluetooth（直接）",
                    AudioInputKind.DualSenseBluetooth,
                    BluetoothDevicePath: device.DevicePath)));

            DeviceBox.ItemsSource = choices;
            DeviceBox.SelectedIndex = choices.FindIndex(choice =>
                string.Equals(choice.ConnectionKey, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (DeviceBox.SelectedIndex < 0 && choices.Count > 0)
                DeviceBox.SelectedIndex = 0;
            suppressDeviceSelection = false;

            if (choices.Count == 0)
            {
                StatusText.Text = "DualSenseが見つかりません。接続して再読込してください";
                return;
            }

            ConnectSelectedInput();
        }
        catch (Exception ex)
        {
            suppressDeviceSelection = false;
            StatusText.Text = $"DualSenseの検索に失敗しました: {ex.Message}";
        }
    }

    static bool IsDualSenseAudioDevice(MMDevice device)
    {
        string name = device.FriendlyName;
        return name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ワイヤレス コントローラー", StringComparison.OrdinalIgnoreCase);
    }

    void ConnectionTimer_Tick(object? sender, EventArgs e)
    {
        if (busy || IsRecording) return;

        try
        {
            var bluetoothPaths = DualSenseBluetoothCapture.EnumerateConnected()
                .Select(device => device.DevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usbPaths = DualSenseMuteButtonMonitor.EnumerateConnectedUsb()
                .Select(device => device.DevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (DeviceBox.SelectedItem is not AudioInputChoice choice)
            {
                if (bluetoothPaths.Count > 0 || usbPaths.Count > 0)
                    RefreshDevices();
                return;
            }

            bool connected = choice.Kind == AudioInputKind.DualSenseBluetooth
                ? choice.BluetoothDevicePath is not null &&
                  bluetoothPaths.Contains(choice.BluetoothDevicePath)
                : choice.ButtonDevicePath is not null &&
                  usbPaths.Contains(choice.ButtonDevicePath);
            if (!connected)
            {
                StatusText.Text = "DualSenseを再接続しています…";
                RefreshDevices();
            }
        }
        catch
        {
            // A transient device-enumeration failure is retried at the next tick.
        }
    }

    void DisposeDeviceChoices()
    {
        foreach (AudioInputChoice choice in DeviceBox.Items.OfType<AudioInputChoice>())
            choice.WindowsDevice?.Dispose();
        DeviceBox.ItemsSource = null;
    }

    void ConnectSelectedInput()
    {
        if (busy || suppressDeviceSelection || IsRecording) return;
        DisconnectInput();
        if (DeviceBox.SelectedItem is not AudioInputChoice choice) return;

        try
        {
            if (choice.Kind == AudioInputKind.DualSenseBluetooth)
            {
                bluetoothCapture = DualSenseBluetoothCapture.Connect(
                    choice.BluetoothDevicePath!);
                bluetoothCapture.MuteButtonPressed += Controller_MuteButtonPressed;
            }
            else
            {
                usbButtonMonitor = DualSenseMuteButtonMonitor.Connect(
                    choice.ButtonDevicePath!);
                usbButtonMonitor.MuteButtonPressed += Controller_MuteButtonPressed;
            }

            StatusText.Text = HasValidModel
                ? "ミュート中 — DualSenseのマイクボタンで音声入力を開始"
                : "ミュート中 — 先に認識モデルを準備してください";
        }
        catch (Exception ex)
        {
            DisconnectInput();
            StatusText.Text = $"DualSenseへ接続できません: {ex.Message}";
        }
    }

    void DisconnectInput()
    {
        if (bluetoothCapture is not null)
        {
            bluetoothCapture.MuteButtonPressed -= Controller_MuteButtonPressed;
            bluetoothCapture.Dispose();
            bluetoothCapture = null;
        }

        if (usbButtonMonitor is not null)
        {
            usbButtonMonitor.MuteButtonPressed -= Controller_MuteButtonPressed;
            usbButtonMonitor.Dispose();
            usbButtonMonitor = null;
        }
    }

    void Controller_MuteButtonPressed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = ToggleFromControllerAsync()));

    async Task ToggleFromControllerAsync()
    {
        if (busy || DeviceBox.SelectedItem is not AudioInputChoice) return;
        if (IsRecording)
            await MuteAndTranscribeAsync();
        else
            StartListening();
    }

    void StartListening()
    {
        if (!HasValidModel)
        {
            StatusText.Text = "ミュート中 — 先に認識モデルを準備してください";
            return;
        }
        if (DeviceBox.SelectedItem is not AudioInputChoice choice)
        {
            StatusText.Text = "DualSenseを接続してください";
            return;
        }
        if ((choice.Kind == AudioInputKind.DualSenseBluetooth && bluetoothCapture is null) ||
            (choice.Kind == AudioInputKind.WindowsAudio && usbButtonMonitor is null))
        {
            StatusText.Text = "DualSenseへ再接続してください";
            return;
        }

        try
        {
            previousWindow = GetForegroundWindow();
            recordingPath = Path.Combine(
                Path.GetTempPath(),
                $"DualSenseVoice-{Guid.NewGuid():N}.wav");

            if (choice.Kind == AudioInputKind.DualSenseBluetooth)
                bluetoothCapture!.StartRecording(recordingPath);
            else
                StartWindowsCapture(choice.WindowsDevice!, recordingPath);

            recordingTimer.Stop();
            recordingTimer.Start();
            StatusCuePlayer.PlayStarted();
            StatusText.Text = $"● マイクON — 話してください — {choice.FriendlyName}";
            DeviceBox.IsEnabled = false;
            RefreshButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            StopWindowsCaptureImmediately();
            DeleteRecordingFile();
            StatusText.Text = $"音声入力を開始できません: {ex.Message}";
        }
    }

    void StartWindowsCapture(MMDevice device, string wavePath)
    {
        windowsCaptureStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        windowsCapture = new WasapiCapture(device);
        windowsWriter = new WaveFileWriter(wavePath, windowsCapture.WaveFormat);
        windowsCapture.DataAvailable += WindowsCapture_DataAvailable;
        windowsCapture.RecordingStopped += WindowsCapture_RecordingStopped;
        windowsCapture.StartRecording();
    }

    void WindowsCapture_DataAvailable(object? sender, WaveInEventArgs e) =>
        windowsWriter?.Write(e.Buffer, 0, e.BytesRecorded);

    void WindowsCapture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        windowsWriter?.Flush();
        if (e.Exception is null)
            windowsCaptureStopped?.TrySetResult();
        else
            windowsCaptureStopped?.TrySetException(e.Exception);
    }

    async Task<double> StopWindowsCaptureAsync()
    {
        WasapiCapture? capture = windowsCapture;
        if (capture is null) return 0;

        TaskCompletionSource? stopped = windowsCaptureStopped;
        capture.StopRecording();
        if (stopped is not null)
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        CleanupWindowsCapture();
        if (recordingPath is null || !File.Exists(recordingPath)) return 0;
        using var reader = new WaveFileReader(recordingPath);
        return reader.TotalTime.TotalSeconds;
    }

    void StopWindowsCaptureImmediately()
    {
        try { windowsCapture?.StopRecording(); }
        catch { }
        CleanupWindowsCapture();
    }

    void CleanupWindowsCapture()
    {
        if (windowsCapture is not null)
        {
            windowsCapture.DataAvailable -= WindowsCapture_DataAvailable;
            windowsCapture.RecordingStopped -= WindowsCapture_RecordingStopped;
            windowsCapture.Dispose();
            windowsCapture = null;
        }
        windowsWriter?.Dispose();
        windowsWriter = null;
        windowsCaptureStopped = null;
    }

    async void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        recordingTimer.Stop();
        if (IsRecording && !busy)
            await MuteAndTranscribeAsync(automatic: true);
    }

    async Task MuteAndTranscribeAsync(bool automatic = false)
    {
        if (!IsRecording || DeviceBox.SelectedItem is not AudioInputChoice choice) return;
        busy = true;
        recordingTimer.Stop();
        DeviceBox.IsEnabled = false;
        RefreshButton.IsEnabled = false;

        try
        {
            double seconds;
            if (choice.Kind == AudioInputKind.DualSenseBluetooth)
            {
                DualSenseBluetoothRecording recording =
                    await bluetoothCapture!.StopRecordingAsync();
                seconds = recording.AudioDuration.TotalSeconds;
                if (recording.DecodedFrames == 0)
                {
                    StatusText.Text = "ミュート中 — 音声を受信できませんでした";
                    return;
                }
            }
            else
            {
                seconds = await StopWindowsCaptureAsync();
            }

            StatusCuePlayer.PlayStopped();
            StatusText.Text = automatic
                ? $"60秒で自動ミュート — 音声 {seconds:F1}秒を文字に変換中…"
                : $"ミュート中 — 音声 {seconds:F1}秒を文字に変換中…";

            using var reader = new WaveFileReader(recordingPath!);
            using var wav = new MemoryStream();
            var resampler = new WdlResamplingSampleProvider(
                reader.ToSampleProvider(),
                16000);
            WaveFileWriter.WriteWavFileToStream(wav, resampler.ToWaveProvider16());
            wav.Position = 0;

            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("ja").Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wav))
                text.Append(segment.Text);

            TranscriptBox.Text = text.ToString().Trim();
            StatusText.Text = TranscriptBox.Text.Length == 0
                ? "ミュート中 — 音声を認識できませんでした"
                : "ミュート中 — 文字起こし完了。もう一度押すと話せます";
            if (AutoPasteBox.IsChecked == true && TranscriptBox.Text.Length > 0)
                PasteToPreviousWindow();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ミュート中 — エラー: {ex.Message}";
        }
        finally
        {
            StopWindowsCaptureImmediately();
            DeleteRecordingFile();
            busy = false;
            DeviceBox.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    void DeleteRecordingFile()
    {
        if (recordingPath is not null && File.Exists(recordingPath))
            File.Delete(recordingPath);
        recordingPath = null;
    }

    void PasteToPreviousWindow()
    {
        System.Windows.Clipboard.SetText(TranscriptBox.Text);
        if (previousWindow == IntPtr.Zero ||
            previousWindow == new WindowInteropHelper(this).Handle)
            return;

        SetForegroundWindow(previousWindow);
        var inputs = new[]
        {
            Key(0x11, false),
            Key(0x56, false),
            Key(0x56, true),
            Key(0x11, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Key(ushort code, bool up) => new()
    {
        type = 1,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = code, dwFlags = up ? 2u : 0u },
        },
    };

    void UpdateModelState()
    {
        ModelStatus.Text = HasValidModel
            ? "準備完了（Whisper base / 日本語）"
            : "初回のみ約142 MBのダウンロードが必要";
        DownloadButton.Visibility = HasValidModel ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        ModelStatus.Text = "モデルをダウンロード中…";
        string temporaryPath = ModelPath + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            await using (var model =
                         await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base))
            await using (var file = File.Create(temporaryPath))
                await model.CopyToAsync(file);
            if (new FileInfo(temporaryPath).Length <= 100_000_000)
                throw new InvalidDataException("モデルファイルが不完全です。");
            File.Move(temporaryPath, ModelPath, true);
            UpdateModelState();
            StatusText.Text = "ミュート中 — DualSenseのマイクボタンで音声入力を開始";
        }
        catch (Exception ex)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            ModelStatus.Text = $"ダウンロード失敗: {ex.Message}";
            DownloadButton.IsEnabled = true;
        }
    }

    void DeviceBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => ConnectSelectedInput();

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptBox.Text.Length > 0)
            System.Windows.Clipboard.SetText(TranscriptBox.Text);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
