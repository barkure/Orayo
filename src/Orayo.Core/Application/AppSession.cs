using System.Threading.Tasks;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.Application;

/// <summary>One shared set of settings and nodes for the lifetime of the application.</summary>
public sealed class AppSession
{
    private readonly IAppStore _store;

    public AppSession(IAppStore store)
    {
        _store = store;
        Catalog = new ServerCatalog(store);
    }

    public AppSettings Settings { get; private set; } = new();
    public AppRuntimeState RuntimeState { get; private set; } = new();
    public ServerCatalog Catalog { get; }

    public async Task LoadAsync()
    {
        Settings = await _store.LoadSettingsAsync().ConfigureAwait(false);
        RuntimeState = await _store.LoadRuntimeStateAsync().ConfigureAwait(false);
        await Catalog.LoadAsync().ConfigureAwait(false);
    }

    // Call after selecting the UI culture, so fallback presets use the chosen language.
    public void NormalizeSettings()
    {
        Settings.RoutingRuleJson = RouteRulePresetService.EnsureRoutingJson(Settings.RoutingRuleJson);
        Settings.DnsJson = DnsPresetService.EnsureDnsJson(Settings.DnsJson);
        if (Settings.LocalSocksPort is < 1 or > 65535) Settings.LocalSocksPort = 10808;
        if (Settings.LocalHttpPort is < 1 or > 65535) Settings.LocalHttpPort = 10809;
    }

    public Task SaveSettingsAsync() => _store.SaveSettingsAsync(Settings);
    public Task SaveRuntimeStateAsync() => _store.SaveRuntimeStateAsync(RuntimeState);
}
