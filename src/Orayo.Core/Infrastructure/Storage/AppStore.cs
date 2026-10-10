using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orayo.Application;
using Orayo.Models;

namespace Orayo.Infrastructure.Storage;

public sealed class AppStore : IAppStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _files;

    public AppStore(AppPaths paths, JsonFileStore files)
    {
        _paths = paths;
        _files = files;
    }

    private string ServersFile => Path.Combine(_paths.DataDirectory, "servers.json");
    private string SettingsFile => Path.Combine(_paths.DataDirectory, "settings.json");
    private string RuntimeStateFile => Path.Combine(_paths.DataDirectory, "runtime_state.json");

    public Task<List<ServerEntry>> LoadServersAsync() => _files.LoadAsync(ServersFile, static () => new List<ServerEntry>());
    public Task SaveServersAsync(IReadOnlyList<ServerEntry> servers) => _files.SaveAsync(ServersFile, servers);
    public Task<AppSettings> LoadSettingsAsync() => _files.LoadAsync(SettingsFile, static () => new AppSettings());
    public Task SaveSettingsAsync(AppSettings settings) => _files.SaveAsync(SettingsFile, settings);
    public Task<AppRuntimeState> LoadRuntimeStateAsync() => _files.LoadAsync(RuntimeStateFile, static () => new AppRuntimeState());
    public Task SaveRuntimeStateAsync(AppRuntimeState runtimeState) => _files.SaveAsync(RuntimeStateFile, runtimeState);
}
