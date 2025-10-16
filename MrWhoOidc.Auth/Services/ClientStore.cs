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
    
    // New methods for multi-secret support
    Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default);
    Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default);
    Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default);
    Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default);
    Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default);
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
        var query = db.Clients
            .AsNoTracking()
            .Include(c => c.ClientSecrets)
            .Where(c => c.ClientId == clientId);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        var client = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (client is null) return false;
        
        // Check new ClientSecrets collection first
        var activeSecrets = client.ClientSecrets
            .Where(s => s.ActivatedAtUtc != null 
                     && s.RevokedAtUtc == null 
                     && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > DateTime.UtcNow))
            .ToList();
        
        if (activeSecrets.Any())
        {
            // Multi-secret validation: check all active secrets
            foreach (var secret in activeSecrets)
            {
                if (!string.IsNullOrEmpty(clientSecret) && hasher.Verify(clientSecret, secret.SecretHash))
                {
                    // Fire-and-forget usage tracking (consider queueing in production)
                    _ = Task.Run(() => RecordSecretUsageAsync(secret.Id, ct), ct);
                    return true;
                }
            }
            return false;
        }
        
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility
        // Fall back to legacy single secret for backward compatibility
        if (string.IsNullOrEmpty(client.ClientSecretHash))
        {
            // Public client: secret not required; allow if no secret provided
            return string.IsNullOrEmpty(clientSecret);
        }
        if (string.IsNullOrEmpty(clientSecret)) return false;
        return hasher.Verify(clientSecret, client.ClientSecretHash);
#pragma warning restore CS0618 // Type or member is obsolete
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

    // Multi-secret management methods

    public async Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default)
    {
        return await db.ClientSecrets
            .AsNoTracking()
            .Where(s => s.ClientId == clientRecordId && s.IsPrimary && s.RevokedAtUtc == null)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default)
    {
        return await db.ClientSecrets
            .AsNoTracking()
            .Where(s => s.ClientId == clientRecordId 
                     && s.ActivatedAtUtc != null 
                     && s.RevokedAtUtc == null
                     && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > DateTime.UtcNow))
            .OrderByDescending(s => s.IsPrimary)
            .ThenByDescending(s => s.ActivatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ClientSecret> CreateSecretAsync(
        Guid clientRecordId, 
        string secretValue, 
        string? description, 
        string? createdBy, 
        DateTime? expiresAtUtc = null, 
        CancellationToken ct = default)
    {
        var secretHash = hasher.Hash(secretValue);
        
        var secret = new ClientSecret
        {
            Id = Guid.NewGuid(),
            ClientId = clientRecordId,
            SecretHash = secretHash,
            Description = description,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAtUtc,
            CreatedBy = createdBy
        };

        db.ClientSecrets.Add(secret);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        
        return secret;
    }

    public async Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
    {
        var secret = await db.ClientSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        
        if (secret == null) return false;
        
        secret.ActivatedAtUtc = DateTime.UtcNow;
        secret.ActivatedBy = activatedBy;
        
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default)
    {
        var secret = await db.ClientSecrets
            .Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        
        if (secret == null) return false;
        
        // Clear IsPrimary flag on all other secrets for this client
        var otherSecrets = await db.ClientSecrets
            .Where(s => s.ClientId == secret.ClientId && s.Id != secretId && s.IsPrimary)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        
        foreach (var other in otherSecrets)
        {
            other.IsPrimary = false;
        }
        
        secret.IsPrimary = true;
        
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
    {
        var secret = await db.ClientSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId, ct)
            .ConfigureAwait(false);
        
        if (secret == null) return false;
        
        // Check that this is not the last active secret
        var activeCount = await db.ClientSecrets
            .Where(s => s.ClientId == secret.ClientId 
                     && s.ActivatedAtUtc != null 
                     && s.RevokedAtUtc == null
                     && s.Id != secretId)
            .CountAsync(ct)
            .ConfigureAwait(false);
        
        if (activeCount == 0)
        {
            // Don't revoke the last active secret (would lock out client)
            return false;
        }
        
        secret.RevokedAtUtc = DateTime.UtcNow;
        secret.RevokedBy = revokedBy;
        secret.IsPrimary = false; // Clear primary flag when revoking
        
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
    {
        try
        {
            var secret = await db.ClientSecrets
                .FirstOrDefaultAsync(s => s.Id == secretId, ct)
                .ConfigureAwait(false);
            
            if (secret == null) return false;
            
            secret.LastUsedAtUtc = DateTime.UtcNow;
            secret.UsageCount++;
            
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Silently fail usage tracking to avoid impacting authentication
            return false;
        }
    }
}
