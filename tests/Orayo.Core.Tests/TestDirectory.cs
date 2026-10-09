using Orayo.Infrastructure.Storage;

namespace Orayo.Core.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Orayo.Tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Root);
    public AppPaths Paths(DeploymentMode mode = DeploymentMode.Portable) => new(
        Path.Combine(Root, "portable", "current"), Path.Combine(Root, "appdata"), mode, Path.Combine(Root, "portable"));
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
