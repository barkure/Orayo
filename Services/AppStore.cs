using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Services;

public class AppStore
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
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
    private string RuntimeStateFile => Path.Combine(_dataDir, "runtime_state.json");
    private string SubscriptionsFile => Path.Combine(_dataDir, "subscriptions.json");

    public AppStore()
    {
        Directory.CreateDirectory(_dataDir);
    }

    public async Task<List<ServerEntry>> LoadServersAsync()
    {
        return await LoadJsonWithBackupAsync(ServersFile, static () => new List<ServerEntry>()).ConfigureAwait(false);
    }

    public Task SaveServersAsync(IReadOnlyList<ServerEntry> servers)
    {
        var json = JsonSerializer.Serialize(servers, JsonOptions);
        return SaveJsonWithBackupAsync(ServersFile, json);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        return await LoadJsonWithBackupAsync(SettingsFile, static () => new AppSettings()).ConfigureAwait(false);
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return SaveJsonWithBackupAsync(SettingsFile, json);
    }

    public async Task<AppRuntimeState> LoadRuntimeStateAsync()
    {
        return await LoadJsonWithBackupAsync(RuntimeStateFile, static () => new AppRuntimeState()).ConfigureAwait(false);
    }

    public Task SaveRuntimeStateAsync(AppRuntimeState runtimeState)
    {
        var json = JsonSerializer.Serialize(runtimeState, JsonOptions);
        return SaveJsonWithBackupAsync(RuntimeStateFile, json);
    }

    public async Task<List<Subscription>> LoadSubscriptionsAsync()
    {
        return await LoadJsonWithBackupAsync(SubscriptionsFile, static () => new List<Subscription>()).ConfigureAwait(false);
    }

    public Task SaveServersAndSubscriptionsAsync(
        IReadOnlyList<ServerEntry> servers,
        IReadOnlyList<Subscription> subscriptions)
    {
        var serversJson = JsonSerializer.Serialize(servers, JsonOptions);
        var subscriptionsJson = JsonSerializer.Serialize(subscriptions, JsonOptions);
        return SaveJsonPairWithBackupAsync(
            ServersFile,
            serversJson,
            SubscriptionsFile,
            subscriptionsJson);
    }

    private static async Task<T> LoadJsonWithBackupAsync<T>(string path, Func<T> fallback)
    {
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryReadJson(path, out T? value))
            {
                return value ?? fallback();
            }

            var backupPath = GetBackupPath(path);
            if (TryReadJson(backupPath, out value))
            {
                TryCopyFile(backupPath, path);
                return value ?? fallback();
            }

            return fallback();
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task SaveJsonWithBackupAsync(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await FileLock.WaitAsync().ConfigureAwait(false);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = GetBackupPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Copy(path, backupPath, overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
            FileLock.Release();
        }
    }

    private static async Task SaveJsonPairWithBackupAsync(
        string firstPath,
        string firstJson,
        string secondPath,
        string secondJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);

        await FileLock.WaitAsync().ConfigureAwait(false);
        var transactionId = Guid.NewGuid().ToString("N");
        var firstTemp = firstPath + "." + transactionId + ".tmp";
        var secondTemp = secondPath + "." + transactionId + ".tmp";
        var firstRollback = firstPath + "." + transactionId + ".rollback";
        var secondRollback = secondPath + "." + transactionId + ".rollback";
        var firstExisted = File.Exists(firstPath);
        var secondExisted = File.Exists(secondPath);

        try
        {
            await File.WriteAllTextAsync(firstTemp, firstJson).ConfigureAwait(false);
            await File.WriteAllTextAsync(secondTemp, secondJson).ConfigureAwait(false);

            if (firstExisted)
            {
                File.Copy(firstPath, firstRollback, overwrite: true);
            }
            if (secondExisted)
            {
                File.Copy(secondPath, secondRollback, overwrite: true);
            }

            File.Move(firstTemp, firstPath, overwrite: true);
            File.Move(secondTemp, secondPath, overwrite: true);

            if (firstExisted)
            {
                File.Copy(firstRollback, GetBackupPath(firstPath), overwrite: true);
            }
            if (secondExisted)
            {
                File.Copy(secondRollback, GetBackupPath(secondPath), overwrite: true);
            }
        }
        catch
        {
            RestoreTransactionFile(firstPath, firstRollback, firstExisted);
            RestoreTransactionFile(secondPath, secondRollback, secondExisted);
            throw;
        }
        finally
        {
            TryDeleteFile(firstTemp);
            TryDeleteFile(secondTemp);
            TryDeleteFile(firstRollback);
            TryDeleteFile(secondRollback);
            FileLock.Release();
        }
    }

    private static bool TryReadJson<T>(string path, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string GetBackupPath(string path) => path + ".bak";

    private static void TryCopyFile(string sourcePath, string targetPath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void RestoreTransactionFile(string path, string rollbackPath, bool existed)
    {
        try
        {
            if (existed && File.Exists(rollbackPath))
            {
                File.Copy(rollbackPath, path, overwrite: true);
            }
            else if (!existed && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
