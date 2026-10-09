using Orayo.Infrastructure.Storage;
using Orayo.Services;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class PendingUpdateTests
{
    [Fact]
    public void Pending_core_update_survives_portable_package_relocation()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        var stagedDirectory = Path.Combine(directory.Root, "staged");
        Directory.CreateDirectory(stagedDirectory);
        var stagedFile = Path.Combine(stagedDirectory, "xray.exe");
        File.WriteAllText(stagedFile, "updated core");
        using var update = new CoreUpdateService.StagedXrayCoreUpdate(stagedDirectory, stagedFile);
        new CoreUpdateService(paths).StagePendingXrayCoreUpdate(update);

        var movedRoot = Path.Combine(directory.Root, "moved");
        Directory.Move(Path.Combine(directory.Root, "portable"), movedRoot);
        var movedPaths = new AppPaths(Path.Combine(movedRoot, "current"), Path.Combine(directory.Root, "appdata"),
            DeploymentMode.Portable, movedRoot);
        Assert.True(new CoreUpdateService(movedPaths).TryApplyPendingXrayCoreUpdate());
        Assert.Equal("updated core", File.ReadAllText(Path.Combine(movedPaths.EngineDirectory, "xray.exe")));
        Assert.False(Directory.Exists(movedPaths.PendingUpdateDirectory));
    }
}
