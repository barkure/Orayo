using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Orayo.Launcher;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly bool Chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    [STAThread]
    private static int Main(string[] args)
    {
        // No install/update hooks are registered. Velopack probes the native entry point.
        if (args.Length > 0 && args[0].StartsWith("--veloapp-", StringComparison.Ordinal))
        {
            if (args[0] == "--veloapp-version") Console.WriteLine("0.0.1298");
            return 0;
        }

        try
        {
            var architecture = RuntimeRequirements.ArchitectureName(RuntimeInformation.ProcessArchitecture);
            var appPath = Path.Combine(AppContext.BaseDirectory, "Orayo.App.exe");
            if (!File.Exists(appPath)) throw new FileNotFoundException("Orayo.App.exe is missing.");
            while (true)
            {
                var missingDotnet = !HasDotnet(architecture);
                var missingWindows = !HasWindowsRuntime();
                if (args.Length == 1 && args[0] == "--check-runtime")
                    return (missingDotnet ? 1 : 0) | (missingWindows ? 2 : 0);
                if (!missingDotnet && !missingWindows) break;

                var choice = NativeDialog.Show(Chinese, architecture, missingDotnet, missingWindows);
                if (choice == 0 || choice == 2) return 0;
                if (choice == 100)
                    Open(RuntimeRequirements.DotnetDownload(architecture, Chinese));
                if (choice == 101)
                    Open(RuntimeRequirements.WindowsDownload(architecture));
                // The dialog returns after a download or retry, then probes both dependencies again.
            }

            var start = new ProcessStartInfo(appPath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Orayo.");
            // Preserve the helper's parent PID semantics: UI and helper still run in Orayo.App.exe.
            return 0;
        }
        catch (Exception ex)
        {
            NativeDialog.Error(Chinese ? "无法启动 Orayo" : "Unable to start Orayo", ex.Message);
            return 1;
        }
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static bool HasDotnet(string architecture)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = registry.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\{architecture}");
        var root = key?.GetValue("InstallLocation") as string;
        return !string.IsNullOrEmpty(root) && RuntimeRequirements.HasDesktopRuntime(root);
    }

    private static bool HasWindowsRuntime()
    {
        try
        {
            // Use the same bootstrapper shipped with the UI, not a loose registry-name match.
            var result = BootstrapInitialize(0x00020000, "", 0);
            if (result < 0) return false;
            BootstrapShutdown();
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", EntryPoint = "MddBootstrapInitialize", CharSet = CharSet.Unicode)]
    private static extern int BootstrapInitialize(uint majorMinorVersion, string versionTag, ulong minimumVersion);
    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", EntryPoint = "MddBootstrapShutdown")]
    private static extern void BootstrapShutdown();
}
