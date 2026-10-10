using System.Text.Json;
using Orayo.Application;
using Orayo.Infrastructure.Storage;
using Orayo.Models;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Portable_data_lives_outside_replaceable_application_files()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        var nextVersion = new AppPaths(Path.Combine(directory.Root, "portable", "app-next"),
            Path.Combine(directory.Root, "appdata"), DeploymentMode.Portable, Path.Combine(directory.Root, "portable"));
        Assert.Equal(Path.Combine(directory.Root, "portable", "data"), paths.DataDirectory);
        Assert.Equal(paths.DataDirectory, nextVersion.DataDirectory);
        Assert.StartsWith(paths.DataDirectory, paths.XrayConfigFile);
        Assert.StartsWith(paths.DataDirectory, paths.CrashLogFile);
        Assert.StartsWith(paths.DataDirectory, paths.PendingUpdateDirectory);
        Assert.StartsWith(paths.DataDirectory, paths.UpdateStagingDirectory);
    }

    [Theory]
    [InlineData(DeploymentMode.Installed)]
    [InlineData(DeploymentMode.Development)]
    public void Installed_and_development_builds_keep_existing_AppData_location(DeploymentMode mode)
    {
        using var directory = new TestDirectory();
        Assert.Equal(Path.Combine(directory.Root, "appdata", "Orayo"), directory.Paths(mode).DataDirectory);
    }

    [Fact]
    public async Task Portable_settings_and_nodes_survive_moving_the_entire_package()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        IAppStore store = new AppStore(paths, new JsonFileStore());
        await store.SaveSettingsAsync(new AppSettings { Language = "en", LocalHttpPort = 12345 });
        var node = new ServerEntry { Name = "Portable node", Host = "example.com", Port = 443, Password = "secret" };
        await store.SaveServersAsync(new[] { node });
        await store.SaveRuntimeStateAsync(new AppRuntimeState { LastSelectedServerId = node.Id });

        var movedRoot = Path.Combine(directory.Root, "moved");
        Directory.Move(Path.Combine(directory.Root, "portable"), movedRoot);
        var movedPaths = new AppPaths(Path.Combine(movedRoot, "current"), Path.Combine(directory.Root, "appdata"),
            DeploymentMode.Portable, movedRoot);
        var moved = new AppStore(movedPaths, new JsonFileStore());
        Assert.Equal(12345, (await moved.LoadSettingsAsync()).LocalHttpPort);
        Assert.Equal("secret", Assert.Single(await moved.LoadServersAsync()).Password);
        Assert.Equal(node.Id, (await moved.LoadRuntimeStateAsync()).LastSelectedServerId);
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "appdata")));
    }

    [Fact]
    public async Task Store_does_not_delete_unrelated_legacy_files()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var legacy = Path.Combine(paths.DataDirectory, "subscriptions.json");
        await File.WriteAllTextAsync(legacy, "legacy content");
        await File.WriteAllTextAsync(legacy + ".bak", "legacy backup");
        var store = new AppStore(paths, new JsonFileStore());
        await store.LoadSettingsAsync();
        await store.SaveSettingsAsync(new AppSettings());
        Assert.Equal("legacy content", await File.ReadAllTextAsync(legacy));
        Assert.Equal("legacy backup", await File.ReadAllTextAsync(legacy + ".bak"));
    }

    [Fact]
    public async Task Corrupt_primary_is_restored_from_last_valid_backup()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        var store = new AppStore(paths, new JsonFileStore());
        await store.SaveSettingsAsync(new AppSettings { LocalHttpPort = 11111 });
        await store.SaveSettingsAsync(new AppSettings { LocalHttpPort = 22222 });
        var file = Path.Combine(paths.DataDirectory, "settings.json");
        await File.WriteAllTextAsync(file, "{ corrupt");
        Assert.Equal(11111, (await store.LoadSettingsAsync()).LocalHttpPort);
        Assert.Equal(11111, JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(file))!.LocalHttpPort);
    }

    [Fact]
    public async Task Saving_over_a_corrupt_primary_preserves_valid_backup()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        var store = new AppStore(paths, new JsonFileStore());
        await store.SaveSettingsAsync(new AppSettings { LocalHttpPort = 11111 });
        await store.SaveSettingsAsync(new AppSettings { LocalHttpPort = 22222 });
        var file = Path.Combine(paths.DataDirectory, "settings.json");
        await File.WriteAllTextAsync(file, "{ corrupt");
        await store.SaveSettingsAsync(new AppSettings { LocalHttpPort = 33333 });
        Assert.Equal(11111, JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(file + ".bak"))!.LocalHttpPort);
        Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Unwritable_portable_data_reports_failure_without_AppData_fallback()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DataDirectory)!);
        // A file in place of the data directory reliably prevents writes on every OS.
        await File.WriteAllTextAsync(paths.DataDirectory, "blocked");
        var store = new AppStore(paths, new JsonFileStore());
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveSettingsAsync(new AppSettings()));
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "appdata")));
    }

    [Fact]
    public async Task Legacy_node_json_loads_without_persisting_presentation_properties()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        var file = Path.Combine(paths.DataDirectory, "servers.json");
        await File.WriteAllTextAsync(file, """[{"Id":"legacy","Host":"example.com","Port":443,"IsActive":true,"DisplayProtocol":"VLESS"}]""");
        var store = new AppStore(paths, new JsonFileStore());
        var nodes = await store.LoadServersAsync();
        Assert.Equal("legacy", Assert.Single(nodes).Id);
        await store.SaveServersAsync(nodes);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.False(document.RootElement[0].TryGetProperty("IsActive", out _));
        Assert.False(document.RootElement[0].TryGetProperty("DisplayProtocol", out _));
    }
}
