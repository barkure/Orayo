using System.Collections.Generic;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Application;

public interface IAppStore
{
    Task<List<ServerEntry>> LoadServersAsync();
    Task SaveServersAsync(IReadOnlyList<ServerEntry> servers);
    Task<AppSettings> LoadSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
    Task<AppRuntimeState> LoadRuntimeStateAsync();
    Task SaveRuntimeStateAsync(AppRuntimeState runtimeState);
}
