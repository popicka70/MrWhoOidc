using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantService
{
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default);
}

internal sealed class TenantService(AuthDbContext db, HybridCache cache) : ITenantService
{
    public async Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var cacheKey = $"tenant:slug:{slug}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string>
        {
            "tenants"
        };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug, cancel),
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"tenant:id:{tenantId}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string>
        {
            "tenants",
            $"tenant:{tenantId}"
        };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancel),
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default)
    {
        var slugCacheKey = $"tenant:slug:{slug}";
        var idCacheKey = $"tenant:id:{tenantId}";
        
        await cache.RemoveAsync(slugCacheKey, ct).ConfigureAwait(false);
        await cache.RemoveAsync(idCacheKey, ct).ConfigureAwait(false);
    }
}
