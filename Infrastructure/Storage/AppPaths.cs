using System;
using System.IO;

namespace Orayo.Infrastructure.Storage;

public enum DeploymentMode
{
    Development,
    Installed,
    Portable
}

/// <summary>Separates replaceable application files from persistent user data.</summary>
public sealed class AppPaths
{
    public AppPaths(string baseDirectory, string localAppDataDirectory, DeploymentMode mode, string? portableRootDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        Mode = mode;
        // Velopack replaces current/ during updates. Portable data belongs beside it.
        var portableRoot = string.IsNullOrWhiteSpace(portableRootDirectory) ? BaseDirectory : portableRootDirectory;
        DataDirectory = mode == DeploymentMode.Portable
            ? Path.Combine(Path.GetFullPath(portableRoot), "data")
            : Path.Combine(Path.GetFullPath(localAppDataDirectory), "Orayo");
    }

    public DeploymentMode Mode { get; }
    public string BaseDirectory { get; }
    public string DataDirectory { get; }
    public string EngineDirectory => Path.Combine(BaseDirectory, "Assets", "engine");
    public string RulesDirectory => Path.Combine(BaseDirectory, "Assets", "rules");
    public string XrayConfigFile => Path.Combine(DataDirectory, "xray_config.json");
    public string CrashLogFile => Path.Combine(DataDirectory, "crash.log");
    public string PendingUpdateDirectory => Path.Combine(DataDirectory, "pending-update");
    public string UpdateStagingDirectory => Path.Combine(DataDirectory, "update-staging");
}
