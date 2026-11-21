using System.Collections.Concurrent;
using Kerberos.NET;
using Kerberos.NET.Client;
using Microsoft.Extensions.Logging;

namespace NWmi.Auth.Kerberos;

/// <summary>
/// A ticket cache that reads a credential cache file (MIT format) but never writes it: tickets the client adds
/// (the service tickets) stay in memory beside what the file holds. Kerberos.NET's own file cache writes every added
/// ticket back, which would change the user's file.
/// </summary>
internal sealed class ReadOnlyFileTicketCache(string path, ILoggerFactory? loggerFactory) : ITicketCache2, IDisposable
{
    private readonly Krb5TicketCache _file = new(File.ReadAllBytes(path), loggerFactory) { PersistChanges = false };
    private readonly ConcurrentDictionary<(string Container, string Key), TicketCacheEntry> _added = new();

    public bool RefreshTickets { get; set; }

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    public string? DefaultDomain
    {
        get => _file.DefaultDomain;
        set => _file.DefaultDomain = value;
    }

    public bool Add(TicketCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _added[(entry.Container ?? string.Empty, entry.Key ?? string.Empty)] = entry;
        return true;
    }

    public ValueTask<bool> AddAsync(TicketCacheEntry entry) => new(Add(entry));

    public bool Contains(TicketCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _added.ContainsKey((entry.Container ?? string.Empty, entry.Key ?? string.Empty)) ||
               _file.Contains(entry);
    }

    public ValueTask<bool> ContainsAsync(TicketCacheEntry entry) => new(Contains(entry));

    public object? GetCacheItem(string key, string container)
    {
        if (_added.TryGetValue((container ?? string.Empty, key ?? string.Empty), out var entry))
            return entry.Value;

        return _file.GetCacheItem(key!, container!);
    }

    public T? GetCacheItem<T>(string key, string container) => GetCacheItem(key, container) is T item ? item : default;

    public ValueTask<object?> GetCacheItemAsync(string key, string container) => new(GetCacheItem(key, container));

    public ValueTask<T?> GetCacheItemAsync<T>(string key, string container) => new(GetCacheItem<T>(key, container));

    public IEnumerable<object> GetAll() => _file.GetAll().Concat(_added.Values.Select(e => e.Value!));

    public Task<IEnumerable<object>> GetAllAsync() => Task.FromResult(GetAll());

    public void PurgeTickets() => _added.Clear();

    public Task PurgeTicketsAsync()
    {
        PurgeTickets();
        return Task.CompletedTask;
    }

    public void Dispose() => _file.Dispose();
}
