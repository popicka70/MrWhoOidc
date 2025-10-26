using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantService
{
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default);
    Task<bool> CanProvisionTenantAsync(int additionalCount = 1, CancellationToken ct = default);
}

internal sealed class TenantService(AuthDbContext db, HybridCache cache, ILimitService limitService) : ITenantService
{
    private readonly AuthDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly HybridCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly ILimitService _limitService = limitService ?? throw new ArgumentNullException(nameof(limitService));

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

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await _db.Tenants
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

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await _db.Tenants
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
        
        await _cache.RemoveAsync(slugCacheKey, ct).ConfigureAwait(false);
        await _cache.RemoveAsync(idCacheKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> CanProvisionTenantAsync(int additionalCount = 1, CancellationToken ct = default)
    {
        if (additionalCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalCount), additionalCount, "Additional tenant count must be positive.");
        }

        var activeTenantCount = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return await _limitService.CanAddAsync(LicenseLimitTypes.Tenants, activeTenantCount, additionalCount, null, ct)
            .ConfigureAwait(false);
    }
}
