using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;

namespace MrWhoOidc.WebAuth.Services;

public sealed class DistributedLoginContinuationStore(IDistributedCache cache) : ILoginContinuationStore
{
    private const string KeyPrefix = "loginctx:";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<string> StoreAsync(string continuation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(continuation))
        {
            throw new ArgumentException("Continuation must be non-empty", nameof(continuation));
        }

        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = WebEncoders.Base64UrlEncode(keyBytes);
        var cacheKey = KeyPrefix + key;

        await cache.SetAsync(
            cacheKey,
            Encoding.UTF8.GetBytes(continuation),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
            cancellationToken);

        return key;
    }

    public async Task<string?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var bytes = await cache.GetAsync(KeyPrefix + key, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.CompletedTask;
        }

        return cache.RemoveAsync(KeyPrefix + key, cancellationToken);
    }
}
