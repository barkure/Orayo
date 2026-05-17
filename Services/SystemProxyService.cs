using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Orayo.Services;

public static class SystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private static bool _hasSnapshot;
    private static object? _originalProxyEnable;
    private static object? _originalProxyServer;
    private static object? _originalProxyOverride;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    public static void SetProxy(string host, int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                         ?? throw new InvalidOperationException("Unable to open Internet Settings registry key.");

            CaptureSnapshot(key);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride",
                "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;" +
                "172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;" +
                "172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>",
                RegistryValueKind.String);
            key.Flush();

            NotifyWindows();
        }
        catch
        {
        }
    }

    public static void ClearProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null || !_hasSnapshot)
            {
                return;
            }

            RestoreValue(key, "ProxyEnable", _originalProxyEnable, RegistryValueKind.DWord);
            RestoreValue(key, "ProxyServer", _originalProxyServer, RegistryValueKind.String);
            RestoreValue(key, "ProxyOverride", _originalProxyOverride, RegistryValueKind.String);
            key.Flush();
            _hasSnapshot = false;
            NotifyWindows();
        }
        catch
        {
        }
    }

    private static void CaptureSnapshot(RegistryKey key)
    {
        if (_hasSnapshot)
        {
            return;
        }

        _originalProxyEnable = key.GetValue("ProxyEnable");
        _originalProxyServer = key.GetValue("ProxyServer");
        _originalProxyOverride = key.GetValue("ProxyOverride");
        _hasSnapshot = true;
    }

    private static void RestoreValue(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value, kind);
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}

