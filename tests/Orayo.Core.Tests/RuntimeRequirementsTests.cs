using System.Runtime.InteropServices;
using Orayo.Launcher;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class RuntimeRequirementsTests
{
    [Theory]
    [InlineData("10.0.11", false)]
    [InlineData("10.0.12", true)]
    [InlineData("10.0.13", true)]
    [InlineData("9.0.99", false)]
    [InlineData("11.0.0", false)]
    [InlineData("10.0.12-preview", false)]
    [InlineData("invalid", false)]
    public void Detection_matches_the_application_runtime_version(string version, bool expected)
    {
        Assert.Equal(expected, RuntimeRequirements.IsCompatibleDotnetVersion(version));
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.Arm64, "arm64")]
    public void Download_links_match_application_architecture(Architecture architecture, string name)
    {
        Assert.Equal(name, RuntimeRequirements.ArchitectureName(architecture));
        Assert.Equal($"https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-{name}.exe", RuntimeRequirements.DotnetDownload(name));
        Assert.Equal($"https://aka.ms/windowsappsdk/2.0/2.0.1/windowsappruntimeinstall-{name}.exe", RuntimeRequirements.WindowsDownload(name));
    }

    [Fact]
    public void Core_runtime_alone_does_not_satisfy_desktop_requirement()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "shared", "Microsoft.NETCore.App", "10.0.12"));
        Assert.False(RuntimeRequirements.HasDesktopRuntime(directory.Root));
        Directory.CreateDirectory(Path.Combine(directory.Root, "shared", "Microsoft.WindowsDesktop.App", "9.0.99"));
        Assert.False(RuntimeRequirements.HasDesktopRuntime(directory.Root));
        Directory.CreateDirectory(Path.Combine(directory.Root, "shared", "Microsoft.WindowsDesktop.App", "10.0.12"));
        Assert.True(RuntimeRequirements.HasDesktopRuntime(directory.Root));
    }
}
