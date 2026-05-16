using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Orayo.Helpers;

public static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private static readonly UISettings UiSettings = new();
    private static readonly List<WindowRegistration> Windows = [];

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    static WindowThemeHelper()
    {
        UiSettings.ColorValuesChanged += (_, _) => RefreshOpenWindows();
    }

    public static void Apply(Window window, bool useMicaBackdrop = true)
    {
        if (useMicaBackdrop)
        {
            window.SystemBackdrop ??= new MicaBackdrop();
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ApplyTitleBar(hwnd);

        lock (Windows)
        {
            Windows.Add(new WindowRegistration(new WeakReference<Window>(window), hwnd));
        }

        window.Activated += (_, _) => ApplyTitleBar(hwnd);
        window.Closed += (_, _) => RemoveWindow(hwnd);
    }

    private static void RefreshOpenWindows()
    {
        List<WindowRegistration> staleReferences = [];

        lock (Windows)
        {
            foreach (var registration in Windows)
            {
                if (!registration.Window.TryGetTarget(out var window))
                {
                    staleReferences.Add(registration);
                    continue;
                }

                window.DispatcherQueue.TryEnqueue(() => ApplyTitleBar(registration.Hwnd));
            }

            foreach (var registration in staleReferences)
            {
                Windows.Remove(registration);
            }
        }
    }

    private static void RemoveWindow(IntPtr hwnd)
    {
        lock (Windows)
        {
            Windows.RemoveAll(registration => registration.Hwnd == hwnd);
        }
    }

    private static void ApplyTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        AppWindow? appWindow;
        try
        {
            appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        }
        catch
        {
            return;
        }

        if (appWindow is null)
        {
            return;
        }

        var isDark = IsDarkMode();
        var darkMode = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var titleBar = appWindow.TitleBar;
        titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        if (isDark)
        {
            ApplyDarkTitleBar(titleBar);
        }
        else
        {
            ApplyLightTitleBar(titleBar);
        }
    }

    public static bool IsDarkMode()
    {
        var color = UiSettings.GetColorValue(UIColorType.Background);
        return color.R < 128 && color.G < 128 && color.B < 128;
    }

    private static void ApplyDarkTitleBar(AppWindowTitleBar titleBar)
    {
        titleBar.ForegroundColor = Colors.White;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveForegroundColor = Color.FromArgb(255, 155, 155, 155);
        titleBar.InactiveBackgroundColor = Colors.Transparent;

        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 64, 64, 64);
        titleBar.ButtonPressedForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 82, 82, 82);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 155, 155, 155);
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private static void ApplyLightTitleBar(AppWindowTitleBar titleBar)
    {
        titleBar.ForegroundColor = Colors.Black;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveForegroundColor = Color.FromArgb(255, 96, 96, 96);
        titleBar.InactiveBackgroundColor = Colors.Transparent;

        titleBar.ButtonForegroundColor = Colors.Black;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverForegroundColor = Colors.Black;
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 229, 229, 229);
        titleBar.ButtonPressedForegroundColor = Colors.Black;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 204, 204, 204);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 96, 96, 96);
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private sealed record WindowRegistration(WeakReference<Window> Window, IntPtr Hwnd);
}
