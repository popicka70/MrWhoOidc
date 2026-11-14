using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// JAR (request object) replay cache over <see cref="IDistributedStateStore"/>. Fails closed: if the
/// store is unavailable the request object is rejected as a possible replay.
/// </summary>
public sealed class StoreJarReplayCache(
    IDistributedStateStore store,
    ILogger<StoreJarReplayCache> logger) : IJarReplayCache
{
    public async ValueTask<bool> TryAddAsync(string key, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var ttl = expiresAt > now ? expiresAt - now : TimeSpan.FromSeconds(1);

        try
        {
            return await store.TryAddAsync($"jar:replay:{key}", "1", ttl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Distributed state unavailable while recording request object jti; rejecting as possible replay.");
            return false;
        }
    }
}
