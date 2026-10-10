using System;
using Orayo.Infrastructure.Storage;
using Orayo.Services;
using Velopack.Locators;

namespace Orayo.Application;

/// <summary>Composition root. Windows construct no storage or runtime services themselves.</summary>
public sealed class AppServices
{
    private AppServices(AppPaths paths, IVelopackLocator locator)
    {
        Paths = paths;
        Session = new AppSession(new AppStore(paths, new JsonFileStore()));
        Runtime = new RuntimeService(paths);
        CoreUpdates = new CoreUpdateService(paths);
        AppUpdates = new AppUpdateService(locator);
    }

    public AppPaths Paths { get; }
    public AppSession Session { get; }
    public RuntimeService Runtime { get; }
    public CoreUpdateService CoreUpdates { get; }
    public AppUpdateService AppUpdates { get; }

    public static AppServices Create()
    {
        var locator = VelopackLocator.Current;
        var mode = locator.IsPortable ? DeploymentMode.Portable
            : locator.CurrentlyInstalledVersion is not null ? DeploymentMode.Installed
            : DeploymentMode.Development;
        var paths = new AppPaths(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), mode, locator.RootAppDir);
        return new AppServices(paths, locator);
    }
}
