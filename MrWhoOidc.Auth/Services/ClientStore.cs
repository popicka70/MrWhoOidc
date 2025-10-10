using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IClientStore
{
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default);
    IQueryable<Client> QueryClients(CancellationToken ct = default);
    /// <summary>
    /// Invalidates cached client metadata for the specified client.
    /// Call this after client updates, deletions, or configuration changes.
    /// </summary>
    Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default);
}

internal sealed class ClientStore(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor, HybridCache cache) : IClientStore
{
    public async Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
        var cacheKey = $"client:metadata:{tenantId}:{clientId}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(15),          // L2 (Redis) expiration
            LocalCacheExpiration = TimeSpan.FromMinutes(5)  // L1 (memory) expiration
        };

        var tags = new List<string>
        {
            "clients",
            $"client:{clientId}",
            $"tenant:{tenantId}"
        };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                var query = db.Clients.AsNoTracking().Where(c => c.ClientId == clientId);

                // Filter by tenant if tenant context is available
                if (tenantAccessor.CurrentTenant != null)
                {
                    query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
                }

                return await query.FirstOrDefaultAsync(cancel);
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
    {
        var query = db.Clients.AsNoTracking().Where(c => c.ClientId == clientId);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        var client = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (client is null) return false;
        if (string.IsNullOrEmpty(client.ClientSecretHash))
        {
            // Public client: secret not required; allow if no secret provided
            return string.IsNullOrEmpty(clientSecret);
        }
        if (string.IsNullOrEmpty(clientSecret)) return false;
        return hasher.Verify(clientSecret, client.ClientSecretHash);
    }

    public IQueryable<Client> QueryClients(CancellationToken ct = default)
    {
        var query = db.Clients.AsQueryable();

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        return query;
    }

    public async Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"client:metadata:{tenantId}:{clientId}";
        await cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
    }
}
