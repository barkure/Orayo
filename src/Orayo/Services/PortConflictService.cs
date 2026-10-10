using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Orayo;

namespace Orayo.Services;

internal static class PortConflictService
{
    public static async Task<string?> EnsurePortsAvailableForCurrentXrayAsync(params int[] ports)
    {
        foreach (var port in ports.Distinct())
        {
            var conflict = await EnsurePortAvailableForCurrentXrayAsync(port);
            if (!string.IsNullOrWhiteSpace(conflict))
            {
                return conflict;
            }
        }

        return null;
    }

    public static async Task<string?> EnsurePortAvailableForCurrentXrayAsync(int port)
    {
        var pid = await FindListeningProcessIdAsync(port);
        if (pid is null)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            var processName = process.ProcessName;
            var processPath = TryGetProcessPath(process);
            var expectedSuffix = Path.Combine("Assets", "engine", "xray.exe");
            var isXray = string.Equals(processName, "xray", StringComparison.OrdinalIgnoreCase);
            var isOrayoManaged = !string.IsNullOrWhiteSpace(processPath)
                && processPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase)
                && processPath.Contains("Orayo", StringComparison.OrdinalIgnoreCase);

            if (isXray && isOrayoManaged)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.Delay(200);
                    return null;
                }
                catch (Exception ex)
                {
                    return string.Format(Strings.ErrPortConflictLegacy, port, ex.Message);
                }
            }

            return string.Format(Strings.ErrPortConflict, port, processName, process.Id, processPath ?? Strings.Unknown);
        }
        catch (Exception ex)
        {
            return string.Format(Strings.ErrPortConflictUnknown, port, ex.Message);
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> FindListeningProcessIdAsync(int port)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalized = Regex.Replace(line.Trim(), @"\s+", " ");
                var parts = normalized.Split(' ');
                if (parts.Length < 5)
                {
                    continue;
                }

                var localEndpoint = parts[1];
                if (!localEndpoint.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }
        }
        catch
        {
        }

        return null;
    }
}

