using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// <see cref="IDistributedCache"/> adapter over <see cref="IDistributedStateStore"/>. This is what
/// HybridCache L2, ASP.NET sessions, login continuations and interaction markers use, so those
/// features become DB-backed (or Redis-backed) through the same provider switch.
/// <para>
/// Cache payloads are Base64-encoded into the store's text value. The small header preserves the
/// sliding/absolute expiration metadata the BCL cache contract expects, because the store only
/// knows an absolute TTL.
/// </para>
/// </summary>
public sealed class DistributedStateCache : IDistributedCache
{
    private const string KeyPrefix = "mrwhooidc:cache:";
    private const string HeaderPrefix = "ds1";
    private readonly IDistributedStateStore _store;
    private readonly TimeSpan _defaultTtl;

    public DistributedStateCache(IDistributedStateStore store, IOptions<DistributedStateOptions> options)
    {
        _store = store;
        var minutes = options.Value.DefaultEntryTtlMinutes <= 0 ? 20 : options.Value.DefaultEntryTtlMinutes;
        _defaultTtl = TimeSpan.FromMinutes(minutes);
    }

    public byte[]? Get(string key)
        => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var raw = await _store.GetAsync(StoreKey(key), token).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }

        var (sliding, absoluteUnixMs, payload) = Parse(raw);
        if (sliding > TimeSpan.Zero)
        {
            var refresh = sliding;
            if (absoluteUnixMs > 0)
            {
                var untilAbsolute = DateTimeOffset.FromUnixTimeMilliseconds(absoluteUnixMs) - DateTimeOffset.UtcNow;
                if (untilAbsolute < refresh)
                {
                    refresh = untilAbsolute;
                }
            }

            if (refresh > TimeSpan.Zero)
            {
                await _store.RefreshAsync(StoreKey(key), refresh, token).ConfigureAwait(false);
            }
        }

        return payload;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var now = DateTimeOffset.UtcNow;
        var (ttl, slidingSeconds, absoluteUnixMs) = ResolveExpiration(options, now);
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"{HeaderPrefix}|{slidingSeconds}|{absoluteUnixMs}|");
        var encoded = header + Convert.ToBase64String(value);
        return _store.SetAsync(StoreKey(key), encoded, ttl, token);
    }

    public void Refresh(string key)
        => RefreshAsync(key).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        var raw = await _store.GetAsync(StoreKey(key), token).ConfigureAwait(false);
        if (raw is null)
        {
            return;
        }

        var (sliding, absoluteUnixMs, _) = Parse(raw);
        var ttl = sliding > TimeSpan.Zero ? sliding : _defaultTtl;
        if (absoluteUnixMs > 0)
        {
            var untilAbsolute = DateTimeOffset.FromUnixTimeMilliseconds(absoluteUnixMs) - DateTimeOffset.UtcNow;
            if (untilAbsolute < ttl)
            {
                ttl = untilAbsolute;
            }
        }

        if (ttl > TimeSpan.Zero)
        {
            await _store.RefreshAsync(StoreKey(key), ttl, token).ConfigureAwait(false);
        }
    }

    public void Remove(string key)
        => RemoveAsync(key).GetAwaiter().GetResult();

    public Task RemoveAsync(string key, CancellationToken token = default)
        => _store.RemoveAsync(StoreKey(key), token);

    private (TimeSpan ttl, int slidingSeconds, long absoluteUnixMs) ResolveExpiration(
        DistributedCacheEntryOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sliding = options.SlidingExpiration ?? TimeSpan.Zero;
        DateTimeOffset? absolute = options.AbsoluteExpiration;
        if (options.AbsoluteExpirationRelativeToNow is { } relative)
        {
            absolute = now.Add(relative);
        }

        TimeSpan ttl;
        if (absolute is { } abs)
        {
            ttl = abs - now;
            if (sliding > TimeSpan.Zero && sliding < ttl)
            {
                ttl = sliding;
            }
        }
        else if (sliding > TimeSpan.Zero)
        {
            ttl = sliding;
        }
        else
        {
            ttl = _defaultTtl;
        }

        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromSeconds(1);
        }

        return (ttl, (int)sliding.TotalSeconds, absolute?.ToUnixTimeMilliseconds() ?? 0);
    }

    private static (TimeSpan sliding, long absoluteUnixMs, byte[] payload) Parse(string raw)
    {
        // Header shape: ds1|slidingSeconds|absoluteUnixMs|base64Payload
        var first = raw.IndexOf('|');
        var second = first < 0 ? -1 : raw.IndexOf('|', first + 1);
        var third = second < 0 ? -1 : raw.IndexOf('|', second + 1);
        if (third < 0 || !raw.AsSpan(0, first).SequenceEqual(HeaderPrefix))
        {
            // Tolerate values written by an older adapter: treat the whole string as Base64.
            return (TimeSpan.Zero, 0, DecodePayload(raw));
        }

        var slidingSeconds = int.TryParse(
            raw.AsSpan(first + 1, second - first - 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedSliding)
            ? Math.Max(0, parsedSliding)
            : 0;

        var absoluteUnixMs = long.TryParse(
            raw.AsSpan(second + 1, third - second - 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedAbsolute)
            ? Math.Max(0, parsedAbsolute)
            : 0;

        return (TimeSpan.FromSeconds(slidingSeconds), absoluteUnixMs, DecodePayload(raw[(third + 1)..]));
    }

    private static byte[] DecodePayload(string payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        var buffer = new byte[payload.Length];
        if (Convert.TryFromBase64String(payload, buffer, out var written))
        {
            return buffer[..written];
        }

        // Last-resort readability: a non-Base64 value is stored as UTF-8 text.
        return Encoding.UTF8.GetBytes(payload);
    }

    private static string StoreKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var full = KeyPrefix + key;
        if (full.Length <= MrWhoOidc.Auth.Persistence.DistributedStateEntry.MaxKeyLength)
        {
            return full;
        }

        // Cache consumers may use keys close to the 512-character limit. Hash oversize keys so the
        // store's key bound is never violated; the digest is deterministic across replicas.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return KeyPrefix + "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
