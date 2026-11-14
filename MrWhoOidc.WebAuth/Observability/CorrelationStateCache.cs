using System;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.WebAuth.Observability;

public interface ICorrelationStateCache
{
    Task<string> StoreAsync(string correlationId, CancellationToken cancellationToken);
    Task<string?> TryGetAsync(string handle, bool consume, CancellationToken cancellationToken);
}

/// <summary>
/// Correlation-handle cache with an in-process L1 and the configured distributed store as L2.
/// The L1 keeps hot handles off the backend; the L2 lets a handle created by one replica be
/// consumed by another.
/// </summary>
internal sealed class CorrelationStateCache(
    IMemoryCache memoryCache,
    IDistributedStateStore store,
    ILogger<CorrelationStateCache> logger,
    IOidcMetrics metrics,
    ICorrelationIdGenerator generator) : ICorrelationStateCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public async Task<string> StoreAsync(string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation ID required", nameof(correlationId));
        }

        var handle = generator.GenerateHandle();
        SetMemory(handle, correlationId);

        try
        {
            await store.SetAsync(StoreKey(handle), correlationId, Ttl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write correlation handle to the distributed state store");
        }

        metrics.CorrelationCacheWrites.Add(1);
        return handle;
    }

    public async Task<string?> TryGetAsync(string handle, bool consume, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        if (memoryCache.TryGetValue<Entry>(MemoryKey(handle), out var entry))
        {
            if (entry.Expiry <= DateTimeOffset.UtcNow)
            {
                memoryCache.Remove(MemoryKey(handle));
                metrics.CorrelationCacheStale.Add(1);
            }
            else
            {
                if (consume)
                {
                    memoryCache.Remove(MemoryKey(handle));
                    // Also consume the L2 entry so another replica cannot resolve the same handle.
                    await RemoveFromStoreAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SetMemory(handle, entry.CorrelationId); // refresh TTL
                }

                metrics.CorrelationCacheHits.Add(1);
                return entry.CorrelationId;
            }
        }

        try
        {
            var cid = await store.GetAsync(StoreKey(handle), cancellationToken).ConfigureAwait(false);
            if (cid is not null)
            {
                if (consume)
                {
                    await RemoveFromStoreAsync(handle, cancellationToken).ConfigureAwait(false);
                    memoryCache.Remove(MemoryKey(handle));
                }
                else
                {
                    await store.RefreshAsync(StoreKey(handle), Ttl, cancellationToken).ConfigureAwait(false);
                    SetMemory(handle, cid);
                }

                metrics.CorrelationCacheHits.Add(1);
                return cid;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to read correlation handle from the distributed state store");
        }

        metrics.CorrelationCacheMisses.Add(1);
        return null;
    }

    private async Task RemoveFromStoreAsync(string handle, CancellationToken cancellationToken)
    {
        try
        {
            await store.RemoveAsync(StoreKey(handle), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to consume correlation handle from the distributed state store");
        }
    }

    private void SetMemory(string handle, string correlationId)
    {
        var entry = new Entry(correlationId, DateTimeOffset.UtcNow.Add(Ttl));
        memoryCache.Set(MemoryKey(handle), entry, entry.Expiry);
    }

    private static string MemoryKey(string handle) => "cid:handle:" + handle;
    private static string StoreKey(string handle) => "cid:handle:" + handle;

    private readonly record struct Entry(string CorrelationId, DateTimeOffset Expiry);
}
