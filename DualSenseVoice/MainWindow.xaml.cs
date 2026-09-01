using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace DualSenseVoice;

public partial class MainWindow : Window
{
    const int HotkeyId = 0x4456;
    readonly MMDeviceEnumerator devices = new();
    WasapiCapture? capture;
    WaveFileWriter? writer;
    string? recordingPath;
    IntPtr previousWindow;
    bool busy;
    string ModelPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseVoice", "ggml-base.bin");
    bool HasValidModel => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 100_000_000;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { RefreshDevices(); UpdateModelState(); RegisterShortcut(); };
        Closed += (_, _) => { UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); capture?.Dispose(); devices.Dispose(); };
    }

    void RefreshDevices()
    {
        DeviceBox.ItemsSource = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        DeviceBox.SelectedIndex = DeviceBox.Items.Cast<MMDevice>().ToList().FindIndex(d => d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase));
        if (DeviceBox.SelectedIndex < 0 && DeviceBox.Items.Count > 0) DeviceBox.SelectedIndex = 0;
    }

    void RegisterShortcut()
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => { if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) { handled = true; _ = ToggleRecordingAsync(); } return IntPtr.Zero; });
        if (!RegisterHotKey(helper.Handle, HotkeyId, 0x0002 | 0x0004, 0x20)) StatusText.Text = "ショートカットを登録できませんでした";
    }

    async Task ToggleRecordingAsync()
    {
        if (busy) return;
        if (capture is null) StartRecording(); else await StopAndTranscribeAsync();
    }

    void StartRecording()
    {
        if (!HasValidModel) { StatusText.Text = "先に認識モデルを準備してください"; return; }
        if (DeviceBox.SelectedItem is not MMDevice device) { StatusText.Text = "マイクを選択してください"; return; }
        try
        {
            previousWindow = GetForegroundWindow();
            recordingPath = Path.Combine(Path.GetTempPath(), $"DualSenseVoice-{Guid.NewGuid():N}.wav");
            capture = new WasapiCapture(device);
            writer = new WaveFileWriter(recordingPath, capture.WaveFormat);
            capture.DataAvailable += (_, e) => writer?.Write(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, _) => writer?.Flush();
            capture.StartRecording();
            StatusText.Text = $"● 録音中 — {device.FriendlyName}";
            RecordButton.Content = "停止して文字化";
            RecordButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 69, 91));
        }
        catch (Exception ex)
        {
            capture?.Dispose(); capture = null;
            writer?.Dispose(); writer = null;
            if (recordingPath is not null && File.Exists(recordingPath)) File.Delete(recordingPath);
            StatusText.Text = $"録音を開始できません: {ex.Message}";
        }
    }

    async Task StopAndTranscribeAsync()
    {
        busy = true;
        RecordButton.IsEnabled = false;
        capture!.StopRecording();
        capture.Dispose(); capture = null;
        writer?.Dispose(); writer = null;
        StatusText.Text = "音声を文字に変換中…";
        try
        {
            using var reader = new WaveFileReader(recordingPath!);
            using var wav = new MemoryStream();
            var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
            WaveFileWriter.WriteWavFileToStream(wav, resampler.ToWaveProvider16());
            wav.Position = 0;
            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("ja").Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wav)) text.Append(segment.Text);
            TranscriptBox.Text = text.ToString().Trim();
            StatusText.Text = TranscriptBox.Text.Length == 0 ? "音声を認識できませんでした" : "文字起こし完了";
            if (AutoPasteBox.IsChecked == true && TranscriptBox.Text.Length > 0) PasteToPreviousWindow();
        }
        catch (Exception ex) { StatusText.Text = $"エラー: {ex.Message}"; }
        finally
        {
            if (recordingPath is not null) File.Delete(recordingPath);
            busy = false; RecordButton.IsEnabled = true; RecordButton.Content = "録音を開始";
            RecordButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(89, 103, 232));
        }
    }

    void PasteToPreviousWindow()
    {
        Clipboard.SetText(TranscriptBox.Text);
        if (previousWindow == IntPtr.Zero || previousWindow == new WindowInteropHelper(this).Handle) return;
        SetForegroundWindow(previousWindow);
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Key(ushort code, bool up) => new() { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = code, dwFlags = up ? 2u : 0u } } };
    void UpdateModelState() { ModelStatus.Text = HasValidModel ? "準備完了（Whisper base / 日本語）" : "初回のみ約142 MBのダウンロードが必要"; DownloadButton.Visibility = HasValidModel ? Visibility.Collapsed : Visibility.Visible; }
    async void Download_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false; ModelStatus.Text = "モデルをダウンロード中…";
        var temporaryPath = ModelPath + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base))
            await using (var file = File.Create(temporaryPath)) await model.CopyToAsync(file);
            if (new FileInfo(temporaryPath).Length <= 100_000_000) throw new InvalidDataException("モデルファイルが不完全です。");
            File.Move(temporaryPath, ModelPath, true);
            UpdateModelState();
        }
        catch (Exception ex)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            ModelStatus.Text = $"ダウンロード失敗: {ex.Message}"; DownloadButton.IsEnabled = true;
        }
    }
    async void Record_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();
    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    void Copy_Click(object sender, RoutedEventArgs e) { if (TranscriptBox.Text.Length > 0) Clipboard.SetText(TranscriptBox.Text); }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
}
