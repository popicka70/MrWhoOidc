using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Security;

public interface IPublicJwksCache
{
    Task<(string etag, string json)> GetClientAsync(string clientId, CancellationToken ct);

    /// <summary>
    /// The public JWKS of a provider.
    /// <para>
    /// M-Jwks: provider names are unique only per tenant, so the lookup and the cache key are tenant
    /// qualified. Without that, one tenant could shadow another tenant's provider keys.
    /// </para>
    /// </summary>
    Task<(string etag, string json)> GetProviderAsync(string providerName, Guid? tenantId, CancellationToken ct);

    /// <summary>
    /// The aggregated public JWKS of the tenant's (and the platform's) providers.
    /// </summary>
    Task<(string etag, string json)> GetAllProvidersAsync(Guid? tenantId, CancellationToken ct);

    Task InvalidateClientAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached JWKS of a provider name in every tenant (the entries are tenant qualified, so the
    /// invalidation is by tag).
    /// </summary>
    Task InvalidateProviderAsync(string providerName, CancellationToken ct = default);

    Task InvalidateAllProvidersAsync(CancellationToken ct = default);
}

public sealed class PublicJwksCache : IPublicJwksCache
{
    private static class EventIds
    {
        public static readonly EventId ZeroKeysJarEnabled = new(5100, nameof(ZeroKeysJarEnabled));
        public static readonly EventId ZeroKeysActiveNonPublishable = new(5101, nameof(ZeroKeysActiveNonPublishable));
    }
    private readonly HybridCache _cache;
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    private readonly IOptions<AuthOptions> _options;
    private readonly ILogger<PublicJwksCache> _logger;
    private readonly Observability.IOidcMetrics _metrics;

    // metrics parameter made optional to avoid breaking lightweight test hosts that haven't registered OidcMetrics yet
    private readonly ISecretProtector? _secretProtector;

    public PublicJwksCache(HybridCache cache, IDbContextFactory<AuthDbContext> dbFactory, IOptions<AuthOptions> options, ILogger<PublicJwksCache> logger, Observability.IOidcMetrics metrics, ISecretProtector? secretProtector = null)
    {
        _secretProtector = secretProtector;
        _cache = cache;
        _dbFactory = dbFactory;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvalidateClientAsync(string clientId, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(ClientKey(clientId), ct);
    }

    public async Task InvalidateProviderAsync(string providerName, CancellationToken ct = default)
    {
        // The provider entries are tenant qualified, so the invalidation is by tag: it drops the entry of
        // every tenant that caches this provider name, plus the aggregated entries that contain its keys.
        await _cache.RemoveByTagAsync(ProviderTag(providerName), ct);
        await _cache.RemoveByTagAsync(AllProvidersTag, ct);
    }

    public async Task InvalidateAllProvidersAsync(CancellationToken ct = default)
    {
        await _cache.RemoveByTagAsync(AllProvidersTag, ct);
    }

    public async Task<(string etag, string json)> GetClientAsync(string clientId, CancellationToken ct)
    {
        var cacheKey = ClientKey(clientId);

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),         // L2 (Redis) - longer for keys
            LocalCacheExpiration = TimeSpan.FromMinutes(10) // L1 (memory)
        };

        var tags = new List<string>
        {
            "jwks",
            $"client:{clientId}"
        };

        // Use GetOrCreateAsync pattern - metrics recorded in factory
        var tuple = await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "client"));

                await using var db = await _dbFactory.CreateDbContextAsync(cancel);
                // D17: the factory-created context never carries the request tenant and the cache key is global;
                // client_id is globally unique and a client's public JWKS is public, so the lookup is explicitly
                // cross-tenant (unchanged behaviour).
                var client = await db.Clients.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.ClientId == clientId, cancel);
                string json;
                if (client is null || string.IsNullOrWhiteSpace(client.PublicJwksJson))
                {
                    json = "{\"keys\":[]}";
                }
                else
                {
                    json = NormalizeAndSanitize(client.PublicJwksJson, algOverride: null);
                }
                var etag = ComputeEtag(json);
                return (etag, json);
            },
            options,
            tags,
            ct
        );

        // Note: HybridCache doesn't provide cache hit/miss info directly, so we track misses in factory
        // Hits are implied by not entering the factory
        return tuple;
    }

    public async Task<(string etag, string json)> GetProviderAsync(string providerName, Guid? tenantId, CancellationToken ct)
    {
        var cacheKey = ProviderKey(providerName, tenantId);
        _metrics.ProviderJwksRequests.Add(1, new KeyValuePair<string, object?>("provider", providerName));

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),
            LocalCacheExpiration = TimeSpan.FromMinutes(10)
        };

        var tags = new List<string>
        {
            "jwks",
            AllProvidersTag,
            ProviderTag(providerName)
        };

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "provider"));

                await using var db = await _dbFactory.CreateDbContextAsync(cancel);
                // M-Jwks: provider names are unique only per tenant. The lookup is therefore restricted to the
                // requested tenant plus the platform-wide providers, and the tenant's own provider wins over a
                // platform one with the same name. The factory context carries no tenant, so the tenant is
                // pinned explicitly in the predicate.
                var provider = await db.IdentityProviders.AsNoTracking().IgnoreQueryFilters()
                    .Where(p => p.Name == providerName && p.Enabled && (p.TenantId == null || p.TenantId == tenantId))
                    .OrderBy(p => p.TenantId == null ? 1 : 0)
                    .FirstOrDefaultAsync(cancel);
                if (provider is null)
                {
                    _metrics.ProviderJwksNotFound.Add(1);
                    return ("", "__not_found__");
                }
                var keysQuery = db.IdentityProviderKeys.AsNoTracking().IgnoreQueryFilters()
                    .Where(k => k.IdentityProviderId == provider.Id && k.Active && k.Publishable);
                if (!_options.Value.ProviderJwksIncludeEncryption)
                {
                    keysQuery = keysQuery.Where(k => k.Purpose == IdentityProviderKeyPurpose.Signing);
                }
                var list = await keysQuery.ToListAsync(cancel);
                string json = list.Count == 0 ? "{\"keys\":[]}" : ComposeJwks(list);
                if (list.Count == 0)
                {
                    _metrics.ProviderJwksZeroKeys.Add(1, new KeyValuePair<string, object?>("provider", providerName));
                    try
                    {
                        var jarRequired = provider.ConfigJson != null && Auth.IdentityProviders.OidcProviderConfig.TryParse(provider.ConfigJson, out var parsed).ok && parsed?.UseJAR == true;
                        if (jarRequired)
                        {
                            _logger.LogWarning(EventIds.ZeroKeysJarEnabled, "Provider JWKS served zero keys for JAR-enabled provider {Provider}", providerName);
                        }
                        var hasActiveNonPublishable = await db.IdentityProviderKeys.AsNoTracking().IgnoreQueryFilters().AnyAsync(k => k.IdentityProviderId == provider.Id && k.Active && k.Purpose == IdentityProviderKeyPurpose.Signing && !k.Publishable, cancel);
                        if (hasActiveNonPublishable)
                        {
                            _logger.LogWarning(EventIds.ZeroKeysActiveNonPublishable, "Provider JWKS served zero keys for provider {Provider} but there is at least one ACTIVE non-publishable signing key (likely missing publish step)", providerName);
                        }
                    }
                    catch { }
                }
                var etag = ComputeEtag(json);
                _metrics.ProviderJwksEtagChanges.Add(1, new KeyValuePair<string, object?>("provider", providerName));
                _metrics.ProviderJwksKeysReturned.Add(list.Count, new KeyValuePair<string, object?>("provider", providerName));
                return (etag, json);
            },
            options,
            tags,
            ct
        );
    }

    public async Task<(string etag, string json)> GetAllProvidersAsync(Guid? tenantId, CancellationToken ct)
    {
        var cacheKey = AllProvidersKey(tenantId);
        _metrics.ProviderJwksAllRequests.Add(1);

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),
            LocalCacheExpiration = TimeSpan.FromMinutes(10)
        };

        var tags = new List<string>
        {
            "jwks",
            AllProvidersTag
        };

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "providers_all"));

                await using var db = await _dbFactory.CreateDbContextAsync(cancel);
                // M-Jwks: the aggregate is tenant scoped as well: it must not expose another tenant's
                // provider keys. The factory context carries no tenant, so the tenant is pinned explicitly.
                var providers = await db.IdentityProviders.AsNoTracking().IgnoreQueryFilters()
                    .Where(p => p.Enabled && (p.TenantId == null || p.TenantId == tenantId))
                    .Select(p => p.Id).ToListAsync(cancel);
                var keysQuery = db.IdentityProviderKeys.AsNoTracking().IgnoreQueryFilters().Where(k => providers.Contains(k.IdentityProviderId) && k.Active && k.Publishable);
                if (!_options.Value.ProviderJwksIncludeEncryption)
                    keysQuery = keysQuery.Where(k => k.Purpose == IdentityProviderKeyPurpose.Signing);
                var list = await keysQuery.ToListAsync(cancel);
                string json = list.Count == 0 ? "{\"keys\":[]}" : ComposeJwks(list);
                var etag = ComputeEtag(json);
                _metrics.ProviderJwksEtagChanges.Add(1, new KeyValuePair<string, object?>("provider", "__all__"));
                _metrics.ProviderJwksKeysReturned.Add(list.Count, new KeyValuePair<string, object?>("provider", "__all__"));
                return (etag, json);
            },
            options,
            tags,
            ct
        );
    }

    private static string ClientKey(string clientId) => $"jwks:client:{clientId}";

    /// <summary>
    /// The cache key of a provider's JWKS. It is tenant qualified: provider names are unique only per
    /// tenant, so a global key would let one tenant serve (or shadow) another tenant's provider keys.
    /// </summary>
    private static string ProviderKey(string name, Guid? tenantId) => $"jwks:provider:{TenantScope(tenantId)}:{name}";

    private static string AllProvidersKey(Guid? tenantId) => $"jwks:providers:all:{TenantScope(tenantId)}";

    /// <summary>The scope part of a tenant qualified cache key.</summary>
    private static string TenantScope(Guid? tenantId) => tenantId?.ToString("N") ?? "platform";

    /// <summary>Tag of a provider's JWKS entry, used to invalidate it in every tenant at once.</summary>
    private static string ProviderTag(string name) => $"provider:{name}";

    /// <summary>Tag of every provider JWKS entry (per provider and aggregated).</summary>
    private const string AllProvidersTag = "providers";

    private string ComposeJwks(IEnumerable<IdentityProviderKey> keys)
    {
        var publicJwks = new List<JsonElement>();
        foreach (var k in keys)
        {
            try
            {
                using var doc = JsonDocument.Parse(_secretProtector.UnprotectProviderKeyJwk(k.Jwk));
                var root = doc.RootElement;
                var sanitized = SanitizeSingleJwk(root, k.Alg, k.Purpose);
                if (sanitized.HasValue) publicJwks.Add(sanitized.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider key parse error (skipping) kid={Kid}", k.Kid);
            }
        }
        // Deduplicate by kid if present
        var dedup = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var je in publicJwks)
        {
            var kid = je.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;
            if (kid != null && !seen.Add(kid))
            {
                _logger.LogWarning("Duplicate kid in provider JWKS output skipped kid={Kid}", kid);
                continue;
            }
            dedup.Add(je);
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("keys");
            writer.WriteStartArray();
            foreach (var je in dedup)
            {
                je.WriteTo(writer);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string NormalizeAndSanitize(string raw, string? algOverride)
    {
        List<JsonElement> keys = new();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in arr.EnumerateArray())
                {
                    var sanitized = SanitizeSingleJwk(k, algOverride, IdentityProviderKeyPurpose.Signing);
                    if (sanitized.HasValue) keys.Add(sanitized.Value);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var sanitized = SanitizeSingleJwk(doc.RootElement, algOverride, IdentityProviderKeyPurpose.Signing);
                if (sanitized.HasValue) keys.Add(sanitized.Value);
            }
        }
        catch { }
        // Deduplicate by kid
        var dedup = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var je in keys)
        {
            var kid = je.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;
            if (kid != null && !seen.Add(kid)) continue;
            dedup.Add(je);
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("keys");
            writer.WriteStartArray();
            foreach (var je in dedup) je.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonElement? SanitizeSingleJwk(JsonElement jwk, string? algOverride, IdentityProviderKeyPurpose purpose)
    {
        if (jwk.ValueKind != JsonValueKind.Object) return null;
        var include = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in jwk.EnumerateObject())
        {
            // Exclude private key material
            if (prop.Name is "d" or "p" or "q" or "dp" or "dq" or "qi" or "oth" or "k" || prop.Name.StartsWith("_"))
                continue;
            include[prop.Name] = prop.Value.Clone();
        }
        // Force alg if override provided and JWK lacks it
        if (!string.IsNullOrWhiteSpace(algOverride) && !include.ContainsKey("alg"))
        {
            include["alg"] = JsonDocument.Parse($"\"{algOverride}\"").RootElement;
        }
        if (!include.ContainsKey("use"))
        {
            var useVal = purpose == IdentityProviderKeyPurpose.Signing ? "sig" : "enc";
            include["use"] = JsonDocument.Parse($"\"{useVal}\"").RootElement;
        }
        // Recompose object
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kv in include)
            {
                writer.WritePropertyName(kv.Key);
                kv.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var doc2 = JsonDocument.Parse(stream.ToArray());
        return doc2.RootElement.Clone();
    }

    private static string ComputeEtag(string json)
    {
        // Hash sorted kids for stability; fallback to full json hash if parse fails
        try
        {
            using var doc = JsonDocument.Parse(json);
            var kids = new List<string>();
            if (doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in arr.EnumerateArray())
                {
                    if (k.TryGetProperty("kid", out var kidProp))
                    {
                        var kid = kidProp.GetString();
                        if (!string.IsNullOrEmpty(kid)) kids.Add(kid!);
                    }
                }
            }
            kids.Sort(StringComparer.Ordinal);
            var joined = string.Join('|', kids);
            return '"' + Sha256Hex(joined) + '"';
        }
        catch
        {
            return '"' + Sha256Hex(json) + '"';
        }
    }

    private static string Sha256Hex(string input) => MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input);
}

