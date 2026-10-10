using Orayo.Models;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Orayo.Services;

/// <summary>Application packaging and update cache are separate from Xray core updates.</summary>
public sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/barkure/Orayo";
    private readonly IVelopackLocator _locator;

    public AppUpdateService(IVelopackLocator locator) => _locator = locator;

    public UpdateManager CreateManager(AppSettings settings)
    {
        var downloader = new OrayoUpdateFileDownloader(
            settings.IsSystemProxyEnabled && !settings.IsTunMode, settings.LocalHttpPort);
        return new UpdateManager(new GithubSource(RepositoryUrl, string.Empty, false, downloader), locator: _locator);
    }

}
