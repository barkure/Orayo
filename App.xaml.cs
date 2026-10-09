using Microsoft.UI.Xaml;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;
using Orayo.Application;
using Orayo.Services;
using Velopack;

namespace Orayo;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string SingleInstanceMutexName = @"Local\Orayo.SingleInstance";
    private const string ShowWindowEventName = @"Local\Orayo.ShowWindow";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    private AppServices? _services;
    private readonly TunHelperLaunchOptions? _tunHelperOptions;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    public App()
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        var helperRequested = TunHelperProtocol.IsHelperInvocation(commandLineArgs);
        TunHelperLaunchOptions.TryParse(commandLineArgs, out var helperOptions);
        _tunHelperOptions = helperOptions;

        if (helperRequested && _tunHelperOptions is null)
        {
            Environment.Exit(2);
            return;
        }

        if (_tunHelperOptions is null)
        {
            VelopackApp.Build().Run();
            if (!TryClaimSingleInstance(SingleInstanceMutexName))
            {
                SignalExistingInstance();
                Environment.Exit(0);
                return;
            }
        }
        else
        {
            return;
        }

        _services = AppServices.Create();
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            if (_services is null) return;
            Directory.CreateDirectory(_services.Paths.DataDirectory);
            File.AppendAllText(
                _services.Paths.CrashLogFile,
                $"[{DateTimeOffset.Now:O}] {e.Exception}\r\n\r\n");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_tunHelperOptions is not null)
        {
            var helper = new TunHelperHost(_tunHelperOptions);
            Task.Run(() => helper.RunAsync()).GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        var services = _services ?? throw new InvalidOperationException("Application services have not been initialized.");
        services.CoreUpdates.TryApplyPendingXrayCoreUpdate();
        services.Session.LoadAsync().GetAwaiter().GetResult();
        var settings = services.Session.Settings;
        if (!string.IsNullOrEmpty(settings.Language))
        {
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo(settings.Language);
            }
            catch
            {
            }
        }

        services.Session.NormalizeSettings();
        var window = new MainWindow(services);

        _window = window;
        InitializeTrayIcon();
        StartShowWindowListener();
        _window.Activate();

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

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "AppIcon.ico");
        var icon = System.IO.File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.TrayShowOrayo, null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.TrayExit, null, async (_, _) => await RequestShutdownAsync());

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
        if (_services is not null)
            await _services.Runtime.StopForShutdownAsync();
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
