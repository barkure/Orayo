using System;
using Microsoft.Win32;

namespace Orayo.Services;

public static class AutoStartService
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "Orayo";
    private const string AutoStartArgument = "--autostart";

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return true;
            }

            var exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            key.SetValue(RunValueName, $"\"{exePath}\" {AutoStartArgument}", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
