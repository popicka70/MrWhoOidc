using System.Collections.Concurrent;
using System.Globalization;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// In-process <see cref="IDistributedStateStore"/>. Only safe with a single replica; intended for
/// tests, local development, and explicit <c>DistributedState:Provider=Memory</c> deployments.
/// </summary>
public sealed class MemoryDistributedStateStore : IDistributedStateStore
{
    private sealed record Entry(string Value, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<bool> TryAddAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_entries.TryGetValue(key, out var existing) && existing.ExpiresAtUtc > now)
            {
                return Task.FromResult(false);
            }

            _entries[key] = new Entry(value, now.Add(NormalizeTtl(ttl)));
            return Task.FromResult(true);
        }
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<string?>(null);
            }

            if (entry.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _entries.TryRemove(key, out _);
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(entry.Value);
        }
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            _entries[key] = new Entry(value, DateTime.UtcNow.Add(NormalizeTtl(ttl)));
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            return Task.FromResult(_entries.TryRemove(key, out _));
        }
    }

    public Task<bool> RefreshAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(key, out _);
                return Task.FromResult(false);
            }

            _entries[key] = entry with { ExpiresAtUtc = now.Add(NormalizeTtl(ttl)) };
            return Task.FromResult(true);
        }
    }

    public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            long count;
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAtUtc <= now)
            {
                count = 1;
                _entries[key] = new Entry("1", now.Add(NormalizeTtl(ttl)));
            }
            else
            {
                count = long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existing)
                    ? existing + 1
                    : 1;
                _entries[key] = entry with { Value = count.ToString(CultureInfo.InvariantCulture) };
            }

            return Task.FromResult(count);
        }
    }

    public Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(key, out _);
                return Task.FromResult<TimeSpan?>(null);
            }

            return Task.FromResult<TimeSpan?>(entry.ExpiresAtUtc - now);
        }
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var expired = _entries
                .Where(kv => kv.Value.ExpiresAtUtc <= now)
                .Take(Math.Max(1, batchSize))
                .Select(kv => kv.Key)
                .ToList();

            var removed = 0;
            foreach (var key in expired)
            {
                if (_entries.TryRemove(key, out _))
                {
                    removed++;
                }
            }

            return Task.FromResult(removed);
        }
    }

    private static TimeSpan NormalizeTtl(TimeSpan ttl)
        => ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : ttl;
}
