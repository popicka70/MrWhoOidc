namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// The small Redis key/value contract the host actually relies on: expiring entries, an atomic
/// set-if-absent, and an atomic counter. Two production implementations exist (database and Redis);
/// a memory implementation is available for tests and single-process development.
/// <para>
/// Keys are fully qualified by the caller (for example <c>dpop:replay:{jkt}:{jti}</c>). Values are
/// strings; binary cache payloads are Base64-encoded by the <c>IDistributedCache</c> adapter.
/// </para>
/// </summary>
public interface IDistributedStateStore
{
    /// <summary>
    /// Atomically stores <paramref name="value"/> only when <paramref name="key"/> is absent or its
    /// previous entry has expired. Returns true when the caller won; false when a live entry exists.
    /// </summary>
    Task<bool> TryAddAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Returns the stored value, or null when the key is absent or expired.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores or replaces a value with an absolute expiry.</summary>
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Deletes a key. Returns true when a live key was removed.</summary>
    Task<bool> RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Extends a live key's expiry. Returns false when the key is absent or expired.</summary>
    Task<bool> RefreshAsync(string key, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments the counter stored at <paramref name="key"/> and returns the new value.
    /// A missing or expired counter starts at 1 and receives <paramref name="ttl"/> on first creation.
    /// </summary>
    Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Returns the remaining lifetime of a live key, or null when it is absent/expired.</summary>
    Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes expired entries. The database implementation uses this to keep its table bounded;
    /// implementations whose backend expires keys natively return 0.
    /// </summary>
    Task<int> DeleteExpiredAsync(int batchSize, CancellationToken ct = default);
}
