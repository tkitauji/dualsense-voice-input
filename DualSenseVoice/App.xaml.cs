using System.Windows;

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

    private void ActivateMainWindow()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
