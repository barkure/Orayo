using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.Application;

/// <summary>Owns node edits and share-link imports independently of any window.</summary>
public sealed class ServerCatalog
{
    private readonly IAppStore _store;
    private readonly List<ServerEntry> _servers = [];

    public ServerCatalog(IAppStore store)
    {
        _store = store;
        Servers = _servers.AsReadOnly();
    }

    public IReadOnlyList<ServerEntry> Servers { get; }

    private readonly System.Threading.SemaphoreSlim _mutationLock = new(1, 1);

    public async Task LoadAsync()
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var servers = await _store.LoadServersAsync().ConfigureAwait(false);
            _servers.Clear();
            _servers.AddRange(servers);
        }
        finally { _mutationLock.Release(); }
    }

    public ServerEntry? ResolveSelection(string? lastSelectedId) =>
        _servers.FirstOrDefault(x => x.Id == lastSelectedId) ?? _servers.FirstOrDefault();

    public Task<IReadOnlyList<ServerEntry>> ImportAsync(string text) => MutateAsync<IReadOnlyList<ServerEntry>>(updated =>
    {
        var imported = new List<ServerEntry>();
        foreach (var token in Regex.Split(text, @"\s+"))
        {
            var server = NodeLinkParser.Parse(token);
            if (server is null || updated.Any(x =>
                x.Protocol == server.Protocol && x.Host == server.Host && x.Port == server.Port && x.Name == server.Name))
            {
                continue;
            }
            updated.Add(server);
            imported.Add(server);
        }
        return imported;
    });

    public async Task AddAsync(ServerEntry server) => await MutateAsync(updated =>
    {
        updated.Add(server);
        return true;
    }).ConfigureAwait(false);

    public Task<bool> ReplaceAsync(ServerEntry original, ServerEntry replacement) => MutateAsync(updated =>
    {
        var index = updated.IndexOf(original);
        if (index < 0) return false;
        replacement.Id = original.Id;
        updated[index] = replacement;
        return true;
    });

    public Task<bool> RemoveAsync(ServerEntry server) => MutateAsync(updated => updated.Remove(server));

    private async Task<TResult> MutateAsync<TResult>(Func<List<ServerEntry>, TResult> edit)
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var updated = _servers.ToList();
            var result = edit(updated);
            if (!updated.SequenceEqual(_servers))
            {
                // Commit to memory only after persistence succeeds.
                await _store.SaveServersAsync(updated).ConfigureAwait(false);
                _servers.Clear();
                _servers.AddRange(updated);
            }
            return result;
        }
        finally { _mutationLock.Release(); }
    }
}
