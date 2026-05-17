using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;

namespace Orayo.Services;

public static class SystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orayo");
    private static readonly string SnapshotPath = Path.Combine(DataDir, "proxy_snapshot.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static bool _hasSnapshot;
    private static int? _originalProxyEnable;
    private static string? _originalProxyServer;
    private static string? _originalProxyOverride;

    private sealed class ProxySnapshot
    {
        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
    }

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
            if (key is null)
            {
                return;
            }

            EnsureSnapshotLoaded();
            if (!_hasSnapshot)
            {
                return;
            }

            RestoreValue(key, "ProxyEnable", _originalProxyEnable, RegistryValueKind.DWord);
            RestoreValue(key, "ProxyServer", _originalProxyServer, RegistryValueKind.String);
            RestoreValue(key, "ProxyOverride", _originalProxyOverride, RegistryValueKind.String);
            key.Flush();
            _hasSnapshot = false;
            TryDeleteFile(SnapshotPath);
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

        if (EnsureSnapshotLoaded())
        {
            return;
        }

        _originalProxyEnable = ReadDWord(key, "ProxyEnable");
        _originalProxyServer = key.GetValue("ProxyServer") as string;
        _originalProxyOverride = key.GetValue("ProxyOverride") as string;
        _hasSnapshot = true;
        SaveSnapshot();
    }

    private static void RestoreValue(RegistryKey key, string name, int? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value, kind);
    }

    private static void RestoreValue(RegistryKey key, string name, string? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value, kind);
    }

    private static int? ReadDWord(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool EnsureSnapshotLoaded()
    {
        if (_hasSnapshot)
        {
            return true;
        }

        try
        {
            if (!File.Exists(SnapshotPath))
            {
                return false;
            }

            var snapshot = JsonSerializer.Deserialize<ProxySnapshot>(File.ReadAllText(SnapshotPath), JsonOptions);
            if (snapshot is null)
            {
                return false;
            }

            _originalProxyEnable = snapshot.ProxyEnable;
            _originalProxyServer = snapshot.ProxyServer;
            _originalProxyOverride = snapshot.ProxyOverride;
            _hasSnapshot = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveSnapshot()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var snapshot = new ProxySnapshot
            {
                ProxyEnable = _originalProxyEnable,
                ProxyServer = _originalProxyServer,
                ProxyOverride = _originalProxyOverride
            };
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}

