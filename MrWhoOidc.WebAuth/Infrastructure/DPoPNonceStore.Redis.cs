using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Infrastructure;

internal sealed class RedisDPoPNonceStore : MrWhoOidc.Security.IDPoPNonceStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public RedisDPoPNonceStore(IConnectionMultiplexer mux)
    {
        _mux = mux; _db = mux.GetDatabase();
    }

    public async Task<(bool ok, string nonce)> ValidateOrIssueAsync(string endpoint, string clientIp, string? jkt, string? provided, CancellationToken ct = default)
    {
        var key = Key(endpoint, clientIp, jkt);
        var existing = await _db.StringGetAsync(key);
        if (existing.HasValue)
        {
            var existingStr = (string)existing!;
            if (!string.IsNullOrEmpty(provided) && string.Equals(existingStr, provided, StringComparison.Ordinal))
            {
                return (true, existingStr);
            }
            // Issue new
            var nonce = CreateNonce();
            await _db.StringSetAsync(key, nonce, Ttl);
            return (false, nonce);
        }
        else
        {
            var nonce = CreateNonce();
            await _db.StringSetAsync(key, nonce, Ttl);
            return (false, nonce);
        }
    }

    internal static string Key(string endpoint, string clientIp, string? jkt) => $"dpop:nonce:{endpoint}:{HashIp(clientIp)}:{(jkt ?? "no")}";

    // Same scheme as the in-memory store: never put the raw client IP into a Redis key.
    static string HashIp(string clientIp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientIp ?? string.Empty));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
    static string CreateNonce() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
