using Orayo.Application;
using Orayo.Infrastructure.Storage;
using Orayo.Models;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class CatalogTests
{
    private const string Link = "vless://00000000-0000-0000-0000-000000000001@example.com:443?security=tls&type=ws#Alpha";

    [Fact]
    public async Task Import_skips_invalid_links_and_duplicates_in_same_batch_and_existing_nodes()
    {
        using var directory = new TestDirectory();
        var store = new AppStore(directory.Paths(), new JsonFileStore());
        var catalog = new ServerCatalog(store);
        var imported = await catalog.ImportAsync($"invalid\n{Link}\t{Link}");
        Assert.Single(imported);
        Assert.Empty(await catalog.ImportAsync(Link));
        Assert.Equal("Alpha", Assert.Single(await store.LoadServersAsync()).Name);
    }

    [Fact]
    public async Task Concurrent_edits_preserve_all_nodes_in_memory_and_on_disk()
    {
        using var directory = new TestDirectory();
        var store = new AppStore(directory.Paths(), new JsonFileStore());
        var catalog = new ServerCatalog(store);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => catalog.AddAsync(new ServerEntry { Name = $"Node {i}" })));
        Assert.Equal(24, catalog.Servers.Count);
        Assert.Equal(24, (await store.LoadServersAsync()).Count);
    }

    [Fact]
    public async Task Edits_preserve_identity_and_selection_and_remove_persists()
    {
        using var directory = new TestDirectory();
        var store = new AppStore(directory.Paths(), new JsonFileStore());
        var catalog = new ServerCatalog(store);
        var first = new ServerEntry { Name = "First" };
        var second = new ServerEntry { Name = "Second" };
        await catalog.AddAsync(first);
        await catalog.AddAsync(second);
        var replacement = new ServerEntry { Name = "Edited" };
        Assert.True(await catalog.ReplaceAsync(second, replacement));
        Assert.Equal(second.Id, replacement.Id);
        Assert.Same(replacement, catalog.ResolveSelection(second.Id));
        Assert.True(await catalog.RemoveAsync(first));
        var loaded = new ServerCatalog(store);
        await loaded.LoadAsync();
        Assert.Equal("Edited", loaded.ResolveSelection("missing")!.Name);
        Assert.Equal(second.Id, Assert.Single(loaded.Servers).Id);
    }

    [Fact]
    public async Task Failed_save_does_not_publish_an_unsaved_node()
    {
        using var directory = new TestDirectory();
        var paths = directory.Paths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DataDirectory)!);
        await File.WriteAllTextAsync(paths.DataDirectory, "blocked");
        var catalog = new ServerCatalog(new AppStore(paths, new JsonFileStore()));
        await Assert.ThrowsAnyAsync<IOException>(() => catalog.AddAsync(new ServerEntry()));
        Assert.Empty(catalog.Servers);
    }
}
