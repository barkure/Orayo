using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Services;

public class AppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Orayo");

    private string ServersFile => Path.Combine(_dataDir, "servers.json");
    private string SettingsFile => Path.Combine(_dataDir, "settings.json");

    public AppStore()
    {
        Directory.CreateDirectory(_dataDir);
    }

    public async Task<List<ServerEntry>> LoadServersAsync()
    {
        try
        {
            if (!File.Exists(ServersFile))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(ServersFile).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<ServerEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public Task SaveServersAsync(IReadOnlyList<ServerEntry> servers)
    {
        var json = JsonSerializer.Serialize(servers, JsonOptions);
        return File.WriteAllTextAsync(ServersFile, json);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new AppSettings();
            }

            var json = await File.ReadAllTextAsync(SettingsFile).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return File.WriteAllTextAsync(SettingsFile, json);
    }
}

