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
    private const string ShowWindowEventName = @"Local\Orayo.ShowWindow";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;

    public App()
    {
        VelopackApp.Build().Run();
        if (!TryClaimSingleInstance())
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
        var cmdArgs = Environment.GetCommandLineArgs();
        var isTunLaunch = Array.Exists(cmdArgs, arg => string.Equals(arg, "--tun", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow();
        if (isTunLaunch)
        {
            window.SetTunEnabledSilently(true);
        }

        _window = window;
        InitializeTrayIcon();
        StartShowWindowListener();
        _window.Activate();
    }

    public void RequestShutdown(bool fastShutdown = false)
    {
        CleanupOnExit(fastShutdown);
        DisposeTrayIcon();
        ReleaseSingleInstance();
        Environment.Exit(0);
    }

    public void PrepareForRestart(bool fastShutdown = true)
    {
        CleanupOnExit(fastShutdown);
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
        menu.Items.Add("退出", null, (_, _) => RequestShutdown());

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
        _window?.ShowFromTray();
    }

    private static bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
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
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (_showWindowEvent.WaitOne())
                {
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

    private void CleanupOnExit(bool fastShutdown = false)
    {
        SystemProxyService.ClearProxy();

        if (_window is MainWindow mainWindow)
        {
            mainWindow.StopBackgroundServicesOnExit(fastShutdown);
        }
    }

    private static void ReleaseSingleInstance()
    {
        try
        {
            _showWindowEvent?.Set();
            _showWindowEvent?.Dispose();
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
