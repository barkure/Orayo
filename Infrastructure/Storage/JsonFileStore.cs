using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Orayo.Infrastructure.Storage;

/// <summary>Serializes file access and keeps a recoverable copy of the last valid document.</summary>
public sealed class JsonFileStore
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public async Task<T> LoadAsync<T>(string path, Func<T> fallback)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryRead(path, out T? value))
            {
                return value!;
            }

            if (TryRead(path + ".bak", out value))
            {
                try { File.Copy(path + ".bak", path, overwrite: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return value!;
            }

            return fallback();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await _fileLock.WaitAsync().ConfigureAwait(false);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            // A corrupt primary must never overwrite a valid backup.
            if (TryRead<T>(path, out _))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _fileLock.Release();
        }
    }

    private static bool TryRead<T>(string path, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return value is not null;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
