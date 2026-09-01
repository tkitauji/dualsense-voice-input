using System.ComponentModel;
using System.Runtime.Intrinsics.X86;
using System.Windows;
using System.Windows.Media;
using Whisper.net.LibraryLoader;
using MediaColor = System.Windows.Media.Color;
using WpfSystemColors = System.Windows.SystemColors;

namespace DualSenseVoice;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DualSenseVoice.tkitauji.Instance";
    private const string ActivationEventName = @"Local\DualSenseVoice.tkitauji.Activate";

    private Mutex? instanceMutex;
    private EventWaitHandle? activationEvent;
    private RegisteredWaitHandle? activationRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            SignalExistingInstance();
            instanceMutex.Dispose();
            instanceMutex = null;
            Shutdown();
            return;
        }

        activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        ConfigureWhisperRuntime();
        ApplyColorTheme();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(ActivationEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance may still be completing startup.
        }
    }

    private static void ConfigureWhisperRuntime() =>
        RuntimeOptions.RuntimeLibraryOrder = GetWhisperRuntimeOrder(
            SupportsOptimizedWhisperRuntime());

    internal static List<RuntimeLibrary> GetWhisperRuntimeOrder(bool optimizedCpu) =>
        optimizedCpu
            ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.CpuNoAvx];

    internal static bool SupportsOptimizedWhisperRuntime()
    {
        if (!X86Base.IsSupported ||
            !Avx.IsSupported ||
            !Avx2.IsSupported ||
            !Fma.IsSupported)
            return false;

        (_, _, int featureFlags, _) = X86Base.CpuId(1, 0);
        const int f16cFlag = 1 << 29;
        return (featureFlags & f16cFlag) != 0;
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        MainWindow.Activate();
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.BeginInvoke(ApplyColorTheme);
    }

    private void ApplyColorTheme()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["Canvas"] = WpfSystemColors.WindowBrush;
            Resources["Surface"] = WpfSystemColors.WindowBrush;
            Resources["Field"] = WpfSystemColors.WindowBrush;
            Resources["Secondary"] = WpfSystemColors.HighlightBrush;
            Resources["ComboSurface"] = WpfSystemColors.WindowBrush;
            Resources["Ink"] = WpfSystemColors.WindowTextBrush;
            Resources["ButtonInk"] = WpfSystemColors.HighlightTextBrush;
            Resources["Muted"] = WpfSystemColors.WindowTextBrush;
            Resources["Accent"] = WpfSystemColors.HighlightBrush;
            Resources["Action"] = WpfSystemColors.HighlightBrush;
            Resources["ActionHover"] = WpfSystemColors.HotTrackBrush;
            Resources["ActionPressed"] = WpfSystemColors.HighlightBrush;
            return;
        }

        SetBrush("Canvas", 0x0D, 0x12, 0x20);
        SetBrush("Surface", 0x17, 0x1E, 0x2F);
        SetBrush("Field", 0x0F, 0x15, 0x24);
        SetBrush("Secondary", 0x2A, 0x33, 0x4A);
        SetBrush("ComboSurface", 0x20, 0x28, 0x3B);
        SetBrush("Ink", 0xF5, 0xF7, 0xFF);
        SetBrush("ButtonInk", 0xFF, 0xFF, 0xFF);
        SetBrush("Muted", 0x9B, 0xA6, 0xBD);
        SetBrush("Accent", 0x7C, 0x8C, 0xFF);
        SetBrush("Action", 0x4C, 0x59, 0xD1);
        SetBrush("ActionHover", 0x59, 0x67, 0xE8);
        SetBrush("ActionPressed", 0x41, 0x4D, 0xBE);
    }

    private void SetBrush(string key, byte red, byte green, byte blue) =>
        Resources[key] = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        activationRegistration?.Unregister(null);
        activationEvent?.Dispose();
        if (instanceMutex is not null)
        {
            try { instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
