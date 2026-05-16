using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Orayo.Helpers;

namespace Orayo.Services;

public static class AutoStartService
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "Orayo";
    private const string TaskName = "Orayo";

    public static bool Apply(bool enabled, bool tunMode)
    {
        if (!enabled)
        {
            RemoveRunEntry();
            return DeleteScheduledTask();
        }

        var exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        if (tunMode)
        {
            RemoveRunEntry();
            return CreateScheduledTask(exePath);
        }

        if (!DeleteScheduledTask())
        {
            return false;
        }

        return SetRunEntry(exePath);
    }

    private static bool SetRunEntry(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return false;
        }

        key.SetValue(RunValueName, $"\"{exePath}\"");
        return true;
    }

    private static void RemoveRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool CreateScheduledTask(string exePath)
    {
        var arguments = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --tun\" /SC ONLOGON /RL HIGHEST /F";
        return RunSchtasks(arguments, requireElevation: !AdminHelper.IsAdministrator());
    }

    private static bool DeleteScheduledTask()
    {
        if (!IsScheduledTaskPresent())
        {
            return true;
        }

        if (RunSchtasks($"/Delete /TN \"{TaskName}\" /F", requireElevation: false))
        {
            return true;
        }

        return !AdminHelper.IsAdministrator()
            && RunSchtasks($"/Delete /TN \"{TaskName}\" /F", requireElevation: true);
    }

    private static bool IsScheduledTaskPresent()
    {
        return RunSchtasks($"/Query /TN \"{TaskName}\"", requireElevation: false);
    }

    private static bool RunSchtasks(string arguments, bool requireElevation)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = requireElevation,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !requireElevation
        };

        if (requireElevation)
        {
            psi.Verb = "runas";
        }
        else
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
