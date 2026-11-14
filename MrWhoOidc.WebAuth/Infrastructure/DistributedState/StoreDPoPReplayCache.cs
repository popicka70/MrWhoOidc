using Microsoft.Extensions.Logging;
using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// DPoP jti replay cache over <see cref="IDistributedStateStore"/>. Fails closed: if the store is
/// unavailable the proof is rejected as a possible replay rather than accepted unchecked.
/// </summary>
internal sealed class StoreDPoPReplayCache(
    IDistributedStateStore store,
    ILogger<StoreDPoPReplayCache> logger) : IDPoPReplayCache
{
    public async ValueTask<bool> TryAddAsync(string key, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var ttl = expiresAt > DateTimeOffset.UtcNow
            ? expiresAt - DateTimeOffset.UtcNow
            : TimeSpan.FromSeconds(1);

        try
        {
            return await store.TryAddAsync($"dpop:replay:{key}", "1", ttl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Distributed state unavailable while recording DPoP proof jti; rejecting as possible replay.");
            return false;
        }
    }
}
