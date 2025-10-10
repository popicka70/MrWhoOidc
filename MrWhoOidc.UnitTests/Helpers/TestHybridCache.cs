using Microsoft.Extensions.Caching.Hybrid;
using System.Buffers;

namespace MrWhoOidc.UnitTests.Helpers;

/// <summary>
/// A test implementation of HybridCache that always executes the factory (no actual caching).
/// </summary>
public sealed class TestHybridCache : HybridCache
{
    public override async ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        // Always call factory - no caching in tests
        return await factory(state, cancellationToken);
    }

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
