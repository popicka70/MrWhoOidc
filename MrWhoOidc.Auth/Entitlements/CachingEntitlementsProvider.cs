using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Entitlements.Options;

namespace MrWhoOidc.Auth.Entitlements;

public sealed class CachingEntitlementsProvider(
    IMemoryCache cache,
    ILicensingEntitlementsClient client,
    IOptions<LicensingIntegrationOptions> options,
    ILogger<CachingEntitlementsProvider> logger) : IEntitlementsProvider
{
    private static readonly object NegativeCacheSentinel = new();

    public async Task<IReadOnlyDictionary<string, Entitlement>> GetEffectiveEntitlementsAsync(
        string subjectId,
        string? tenantId,
        IReadOnlyCollection<string> productKeys,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            return new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase);
        }

        if (productKeys.Count == 0)
        {
            return new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase);
        }

        var ttl = TimeSpan.FromMinutes(opt.CacheTtlMinutes <= 0 ? 5 : opt.CacheTtlMinutes);

        var result = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase);

        foreach (var productKey in productKeys.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cacheKey = BuildCacheKey(subjectId, tenantId, productKey);
            if (cache.TryGetValue(cacheKey, out var cachedObj))
            {
                if (ReferenceEquals(cachedObj, NegativeCacheSentinel))
                {
                    continue;
                }

                if (cachedObj is Entitlement cachedEntitlement)
                {
                    result[productKey] = cachedEntitlement;
                    continue;
                }
            }

            try
            {
                var response = await client.ResolveEffectiveEntitlementsAsync(
                    new EffectiveEntitlementsRequest
                    {
                        Products = new[] { productKey },
                        Subject = new SubjectContext { Type = "user", Id = subjectId },
                        Tenant = string.IsNullOrWhiteSpace(tenantId) ? null : new TenantContext { Id = tenantId }
                    },
                    issuer,
                    cancellationToken).ConfigureAwait(false);

                if (response.Entitlements.TryGetValue(productKey, out var entitlement))
                {
                    cache.Set(cacheKey, entitlement, ttl);
                    result[productKey] = entitlement;
                }
                else
                {
                    cache.Set(cacheKey, NegativeCacheSentinel, ttl);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed resolving entitlements for product {Product}", productKey);
                // Fail-closed: do not grant entitlement on error.
                cache.Set(cacheKey, NegativeCacheSentinel, ttl);
            }
        }

        return result;
    }

    private static string BuildCacheKey(string subjectId, string? tenantId, string productKey)
        => $"entitlements:{subjectId}:{tenantId ?? "-"}:{productKey}";
}
