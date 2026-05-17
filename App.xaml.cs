using Microsoft.UI.Xaml;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;
using Orayo.Services;
using Velopack;

namespace Orayo;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Orayo.SingleInstance";
    private const string BrokerInstanceMutexName = @"Local\Orayo.TunBroker";
    private const string ShowWindowEventName = @"Local\Orayo.ShowWindow";
    private const string BrokerArgument = "--broker";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    private readonly RuntimeService _runtime = new();
    private readonly bool _isBrokerMode;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    public App()
    {
        VelopackApp.Build().Run();
        var cmdArgs = Environment.GetCommandLineArgs();
        _isBrokerMode = Array.Exists(cmdArgs, arg => string.Equals(arg, BrokerArgument, StringComparison.OrdinalIgnoreCase));
        if (_isBrokerMode && !TryClaimSingleInstance(BrokerInstanceMutexName))
        {
            Environment.Exit(0);
            return;
        }

        if (!_isBrokerMode && !TryClaimSingleInstance(SingleInstanceMutexName))
        {
            SignalExistingInstance();
            Environment.Exit(0);
            return;
        }

        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orayo");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}\r\n\r\n");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_isBrokerMode)
        {
            var broker = new TunBrokerHost();
            Task.Run(() => broker.RunAsync()).GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        var cmdArgs = Environment.GetCommandLineArgs();
        var isAutoStartLaunch = Array.Exists(cmdArgs, arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(_runtime);

        _window = window;
        InitializeTrayIcon();
        StartShowWindowListener();
        if (!isAutoStartLaunch)
        {
            _window.Activate();
        }

        _ = _window.StartAsync();
    }

    public async Task RequestShutdownAsync(bool fastShutdown = false)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (_window is not null)
        {
            await _window.PersistStateForShutdownAsync();
        }

        _window?.HideForShutdown();
        await CleanupOnExitAsync(fastShutdown);
        DisposeTrayIcon();
        ReleaseSingleInstance();
        Environment.Exit(0);
    }

    public async Task PrepareForRestartAsync(bool fastShutdown = true)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (_window is not null)
        {
            await _window.PersistStateForShutdownAsync();
        }

        _window?.HideForShutdown();
        await CleanupOnExitAsync(fastShutdown);
        DisposeTrayIcon();
        ReleaseSingleInstance();
    }

    private void InitializeTrayIcon()
    {
        DisposeTrayIcon();

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var icon = System.IO.File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 Orayo", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await RequestShutdownAsync());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Orayo",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_isExiting)
        {
            return;
        }

        try
        {
            _window?.ShowFromTray();
        }
        catch
        {
        }
    }

    private static bool TryClaimSingleInstance(string mutexName)
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
            signal.Set();
        }
        catch
        {
        }
    }

    private void StartShowWindowListener()
    {
        var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowEvent = showWindowEvent;
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (showWindowEvent.WaitOne())
                {
                    if (_isExiting)
                    {
                        break;
                    }

                    dispatcher.TryEnqueue(ShowMainWindow);
                }
            }
            catch
            {
            }
        });
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private async Task CleanupOnExitAsync(bool fastShutdown = false)
    {
        await _runtime.StopForShutdownAsync();
    }

    private static void ReleaseSingleInstance()
    {
        try
        {
            var showWindowEvent = _showWindowEvent;
            showWindowEvent?.Set();
            showWindowEvent?.Dispose();
            _showWindowEvent = null;
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
        }
    }

}
