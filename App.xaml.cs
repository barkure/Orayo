using Microsoft.UI.Xaml;
using System;
using System.Drawing;
using Forms = System.Windows.Forms;
using Orayo.Services;

namespace Orayo;

public partial class App : Application
{
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;

    public App()
    {
        InitializeComponent();
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
