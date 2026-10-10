using System.Runtime.InteropServices;

namespace Orayo.Launcher;

internal static class RuntimeRequirements
{
    public static readonly Version MinimumDotnetVersion = new(10, 0, 12);
    public const string WindowsRuntimeVersion = "2.0.1";
    public static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Unsupported application architecture.")
    };

    public static bool IsCompatibleDotnetVersion(string version) =>
        Version.TryParse(version, out var parsed)
        && parsed.Major == MinimumDotnetVersion.Major
        && parsed.Minor == MinimumDotnetVersion.Minor
        && parsed >= MinimumDotnetVersion;

    public static bool HasDesktopRuntime(string root) =>
        HasFramework(root, "Microsoft.NETCore.App") && HasFramework(root, "Microsoft.WindowsDesktop.App");

    private static bool HasFramework(string root, string framework)
    {
        var path = Path.Combine(root, "shared", framework);
        return Directory.Exists(path) && Directory.EnumerateDirectories(path)
            .Any(directory => IsCompatibleDotnetVersion(Path.GetFileName(directory)));
    }

    public static string DotnetDownload(string architecture, bool chinese) =>
        $"https://dotnet.microsoft.com/{(chinese ? "zh-cn" : "en-us")}/download/dotnet/thank-you/runtime-desktop-{MinimumDotnetVersion}-windows-{architecture}-installer";

    public static string WindowsDownload(string architecture) =>
        $"https://aka.ms/windowsappsdk/2.0/{WindowsRuntimeVersion}/windowsappruntimeinstall-{architecture}.exe";
}
