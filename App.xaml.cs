using Microsoft.UI.Xaml;
using System;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;
using Orayo.Services;
using Velopack;

namespace Orayo;

public partial class App : Application
{
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;

    public App()
    {
        VelopackApp.Build().Run();
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
        _window.Activate();
    }

    public void RequestShutdown(bool fastShutdown = false)
    {
        CleanupOnExit(fastShutdown);
        DisposeTrayIcon();
        Environment.Exit(0);
    }

    public void PrepareForRestart(bool fastShutdown = true)
    {
        CleanupOnExit(fastShutdown);
        DisposeTrayIcon();
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
}
