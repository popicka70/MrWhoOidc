using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Security;

public interface IPublicJwksCache
{
    Task<(string etag, string json)> GetClientAsync(string clientId, CancellationToken ct);
    Task<(string etag, string json)> GetProviderAsync(string providerName, CancellationToken ct);
    Task<(string etag, string json)> GetAllProvidersAsync(CancellationToken ct);
    void InvalidateClient(string clientId);
    void InvalidateProvider(string providerName);
    void InvalidateAllProviders();
}

public sealed class PublicJwksCache : IPublicJwksCache
{
    private static class EventIds
    {
        public static readonly EventId ZeroKeysJarEnabled = new(5100, nameof(ZeroKeysJarEnabled));
        public static readonly EventId ZeroKeysActiveNonPublishable = new(5101, nameof(ZeroKeysActiveNonPublishable));
    }
    private readonly IMemoryCache _cache;
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    private readonly IOptions<AuthOptions> _options;
    private readonly ILogger<PublicJwksCache> _logger;
    private readonly Observability.IOidcMetrics _metrics;

    // metrics parameter made optional to avoid breaking lightweight test hosts that haven't registered OidcMetrics yet
    public PublicJwksCache(IMemoryCache cache, IDbContextFactory<AuthDbContext> dbFactory, IOptions<AuthOptions> options, ILogger<PublicJwksCache> logger, Observability.IOidcMetrics metrics)
    {
        _cache = cache;
        _dbFactory = dbFactory;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    public void InvalidateClient(string clientId)
    {
        _cache.Remove(ClientKey(clientId));
    }

    public void InvalidateProvider(string providerName)
    {
        _cache.Remove(ProviderKey(providerName));
        _cache.Remove(AllProvidersKey());
    }

    public void InvalidateAllProviders() => _cache.Remove(AllProvidersKey());

    public async Task<(string etag, string json)> GetClientAsync(string clientId, CancellationToken ct)
    {
        var cacheKey = ClientKey(clientId);
        if (_cache.TryGetValue<(string etag, string json)>(cacheKey, out var cached))
        {
            _metrics.ProviderJwksCacheHit.Add(1, new KeyValuePair<string, object?>("scope", "client"));
            return cached;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
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
        var ttl = TimeSpan.FromSeconds(Math.Max(5, _options.Value.ClientJwksCacheSeconds));
        var tuple = (etag, json);
        _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "client"));
        _cache.Set(cacheKey, tuple, ttl);
        return tuple;
    }

    public async Task<(string etag, string json)> GetProviderAsync(string providerName, CancellationToken ct)
    {
        var cacheKey = ProviderKey(providerName);
        _metrics.ProviderJwksRequests.Add(1, new KeyValuePair<string, object?>("provider", providerName));
        if (_cache.TryGetValue<(string etag, string json)>(cacheKey, out var cached))
        {
            _metrics.ProviderJwksCacheHit.Add(1, new KeyValuePair<string, object?>("scope", "provider"));
            return cached;
        }
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == providerName && p.Enabled, ct);
        if (provider is null)
        {
            // Distinguish 404 vs empty caller side; return special marker
            _metrics.ProviderJwksNotFound.Add(1);
            return ("", "__not_found__");
        }
        var keysQuery = db.IdentityProviderKeys.AsNoTracking()
            .Where(k => k.IdentityProviderId == provider.Id && k.Active && k.Publishable);
        if (!_options.Value.ProviderJwksIncludeEncryption)
        {
            keysQuery = keysQuery.Where(k => k.Purpose == IdentityProviderKeyPurpose.Signing);
        }
        var list = await keysQuery.ToListAsync(ct);
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
                // Additional misconfiguration warning: active signing keys exist but none publishable
                var hasActiveNonPublishable = await db.IdentityProviderKeys.AsNoTracking().AnyAsync(k => k.IdentityProviderId == provider.Id && k.Active && k.Purpose == IdentityProviderKeyPurpose.Signing && !k.Publishable, ct);
                if (hasActiveNonPublishable)
                {
                    _logger.LogWarning(EventIds.ZeroKeysActiveNonPublishable, "Provider JWKS served zero keys for provider {Provider} but there is at least one ACTIVE non-publishable signing key (likely missing publish step)", providerName);
                }
            }
            catch { }
        }
        var etag = ComputeEtag(json);
        if (!_cache.TryGetValue<(string etag, string json)>(cacheKey, out var existing) || existing.etag != etag)
        {
            _metrics.ProviderJwksEtagChanges.Add(1, new KeyValuePair<string, object?>("provider", providerName));
        }
        _metrics.ProviderJwksKeysReturned.Add(list.Count, new KeyValuePair<string, object?>("provider", providerName));
        var ttl = TimeSpan.FromSeconds(Math.Max(5, _options.Value.ProviderJwksCacheSeconds));
        var tuple = (etag, json);
        _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "provider"));
        _cache.Set(cacheKey, tuple, ttl);
        return tuple;
    }

    public async Task<(string etag, string json)> GetAllProvidersAsync(CancellationToken ct)
    {
        var cacheKey = AllProvidersKey();
        _metrics.ProviderJwksAllRequests.Add(1);
        if (_cache.TryGetValue<(string etag, string json)>(cacheKey, out var cached))
        {
            _metrics.ProviderJwksCacheHit.Add(1, new KeyValuePair<string, object?>("scope", "providers_all"));
            return cached;
        }
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var providers = await db.IdentityProviders.AsNoTracking().Where(p => p.Enabled).Select(p => p.Id).ToListAsync(ct);
    var keysQuery = db.IdentityProviderKeys.AsNoTracking().Where(k => providers.Contains(k.IdentityProviderId) && k.Active && k.Publishable);
        if (!_options.Value.ProviderJwksIncludeEncryption)
            keysQuery = keysQuery.Where(k => k.Purpose == IdentityProviderKeyPurpose.Signing);
        var list = await keysQuery.ToListAsync(ct);
        string json = list.Count == 0 ? "{\"keys\":[]}" : ComposeJwks(list);
        var etag = ComputeEtag(json);
        if (!_cache.TryGetValue<(string etag, string json)>(cacheKey, out var existingAll) || existingAll.etag != etag)
        {
            _metrics.ProviderJwksEtagChanges.Add(1, new KeyValuePair<string, object?>("provider", "__all__"));
        }
        _metrics.ProviderJwksKeysReturned.Add(list.Count, new KeyValuePair<string, object?>("provider", "__all__"));
        var ttl = TimeSpan.FromSeconds(Math.Max(5, _options.Value.ProviderJwksCacheSeconds));
        var tuple = (etag, json);
        _metrics.ProviderJwksCacheMiss.Add(1, new KeyValuePair<string, object?>("scope", "providers_all"));
        _cache.Set(cacheKey, tuple, ttl);
        return tuple;
    }

    private static string ClientKey(string clientId) => $"jwks:client:{clientId}";
    private static string ProviderKey(string name) => $"jwks:provider:{name}";
    private static string AllProvidersKey() => "jwks:providers:all";

    private string ComposeJwks(IEnumerable<IdentityProviderKey> keys)
    {
        var publicJwks = new List<JsonElement>();
        foreach (var k in keys)
        {
            try
            {
                using var doc = JsonDocument.Parse(k.Jwk);
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

    private static string Sha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
