using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IClientStore
{
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default);
    IQueryable<Client> QueryClients(CancellationToken ct = default);
}

internal sealed class ClientStore(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor) : IClientStore
{
    public Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        var query = db.Clients.AsNoTracking().Where(c => c.ClientId == clientId);
        
        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }
        
        return query.FirstOrDefaultAsync(ct);
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
}
