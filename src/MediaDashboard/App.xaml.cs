using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MediaDashboard.Core;

namespace MediaDashboard;

public partial class App : Application
{
    private const string InstanceName = "MediaDashboard.SingleInstance";
    private const string ShowSignalName = "MediaDashboard.ShowFlyout";
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DEVICECHANGE = 0x0219;

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private AppController? _controller;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private HwndSource? _messageWindow;
    private DispatcherTimer? _hardwareDebounce;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            // Second launch (e.g. double-clicking the exe again): open the running instance's flyout.
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal)) signal.Set();
            Shutdown();
            return;
        }

        Theme.Initialize();
        _controller = new AppController(SettingsStore.Default());
        Loc.Instance.Apply(_controller.Settings.Language);
        _controller.InitializeAutostart();

        _flyout = new FlyoutWindow(_controller);
        _tray = new TrayIcon(_controller, ToggleFlyout, ExitApp);
        ListenForHardwareChanges();
        ListenForShowSignal();
    }

    private void ToggleFlyout()
    {
        if (_flyout!.IsVisible) _flyout.Hide();
        else if (!_flyout.JustClosed) _flyout.ShowNearTray();
    }

    /// <summary>
    /// WM_DISPLAYCHANGE covers the desktop changing; WM_DEVICECHANGE covers a disabled display being
    /// plugged or unplugged (which does not change the desktop). Both are debounced into one re-read.
    /// </summary>
    private void ListenForHardwareChanges()
    {
        _hardwareDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hardwareDebounce.Tick += (_, _) =>
        {
            _hardwareDebounce.Stop();
            _controller!.RefreshDisplays();
        };

        // A hidden top-level window: message-only windows do not receive these broadcasts.
        _messageWindow = new HwndSource(new HwndSourceParameters("MediaDashboard.Messages") { Width = 0, Height = 0, WindowStyle = 0 });
        _messageWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is WM_DISPLAYCHANGE or WM_DEVICECHANGE)
            {
                _hardwareDebounce.Stop();
                _hardwareDebounce.Start();
            }
            return IntPtr.Zero;
        });
    }

    private void ListenForShowSignal()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var thread = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _flyout?.ShowNearTray());
        }) { IsBackground = true, Name = "ShowSignal" };
        thread.Start();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _messageWindow?.Dispose();
        _controller?.Dispose();
        _instance?.ReleaseMutex();
        Shutdown();
    }
}
