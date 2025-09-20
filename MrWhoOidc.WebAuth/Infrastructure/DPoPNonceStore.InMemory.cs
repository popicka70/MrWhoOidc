using System.Collections.Concurrent;

namespace MrWhoOidc.WebAuth.Infrastructure;

internal sealed class InMemoryDPoPNonceStore : IDPoPNonceStore
{
    private record Entry(string Nonce, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public Task<(bool ok, string nonce)> ValidateOrIssueAsync(string endpoint, string clientIp, string? jkt, string? provided, CancellationToken ct = default)
    {
        Cleanup();
        var key = Key(endpoint, clientIp, jkt);
        if (!_store.TryGetValue(key, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var nonce = CreateNonce();
            _store[key] = new Entry(nonce, DateTimeOffset.UtcNow.Add(Ttl));
            return Task.FromResult((false, nonce));
        }
        if (string.IsNullOrEmpty(provided) || !string.Equals(provided, entry.Nonce, StringComparison.Ordinal))
        {
            var nonce = CreateNonce();
            _store[key] = new Entry(nonce, DateTimeOffset.UtcNow.Add(Ttl));
            return Task.FromResult((false, nonce));
        }
        return Task.FromResult((true, entry.Nonce));
    }

    static string Key(string endpoint, string clientIp, string? jkt) => $"dpop:nonce:{endpoint}:{clientIp}:{(jkt ?? "no")}";

    static string CreateNonce() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _store)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _store.TryRemove(kv.Key, out _);
            }
        }
    }
}
