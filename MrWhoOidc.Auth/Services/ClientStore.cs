using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing and retrieving OIDC clients.
/// </summary>
public interface IClientStore
{
    /// <summary>
    /// Finds a client by its public ClientId. Results are cached using HybridCache.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The client if found; otherwise, null.</returns>
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Validates a client secret against the stored hashes. 
    /// Supports both modern multi-secret rotation and legacy single-secret hashes.
    /// Emits authentication metrics for success and failure.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="clientSecret">The plain-text client secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the secret is valid; otherwise, false.</returns>
    Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default);

    /// <summary>
    /// Returns a queryable for clients, respecting tenant isolation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A queryable of clients.</returns>
    IQueryable<Client> QueryClients(CancellationToken ct = default);
    /// <summary>
    /// Invalidates cached client metadata for the specified client.
    /// Call this after client updates, deletions, or configuration changes.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the primary (most recently created) secret for a client.
    /// </summary>
    /// <param name="clientRecordId">The internal database ID of the client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The primary secret if found; otherwise, null.</returns>
    Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default);
    Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default);
    Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default);
    Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default);
    Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default);
}

internal sealed class ClientStore(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    ITenantAccessor tenantAccessor, 
    HybridCache cache,
    ILogger<ClientStore> logger,
    IClientSecretMetrics? metrics = null) : IClientStore
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
        
        var now = DateTime.UtcNow;
        
        // Check new ClientSecrets collection first
        var activeSecrets = client.ClientSecrets
            .Where(s => s.ActivatedAtUtc != null 
                     && s.RevokedAtUtc == null 
                     && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now))
            .ToList();
        
        if (activeSecrets.Any())
        {
            // Multi-secret validation: check all active secrets
            foreach (var secret in activeSecrets)
            {
                if (!string.IsNullOrEmpty(clientSecret) && hasher.Verify(clientSecret, secret.SecretHash))
                {
                    // Record success metric
                    metrics?.AuthenticationSuccess.Add(1, new KeyValuePair<string, object?>("client_id", clientId), new KeyValuePair<string, object?>("is_primary", secret.IsPrimary));
                    
                    // TODO: Fire-and-forget usage tracking causes DbContext concurrency issues.
                    // Should use separate DbContext scope or queue-based approach.
                    // _ = Task.Run(() => RecordSecretUsageAsync(secret.Id, ct), ct);
                    return true;
                }
            }
            
            // Check if secret matched but was expired/revoked
            var expiredSecrets = client.ClientSecrets
                .Where(s => s.ActivatedAtUtc != null 
                         && s.RevokedAtUtc == null 
                         && s.ExpiresAtUtc != null 
                         && s.ExpiresAtUtc <= now)
                .ToList();
            
            foreach (var expiredSecret in expiredSecrets)
            {
                if (!string.IsNullOrEmpty(clientSecret) && hasher.Verify(clientSecret, expiredSecret.SecretHash))
                {
                    // Record failure metric for expired secret
                    metrics?.AuthenticationFailure.Add(1, new KeyValuePair<string, object?>("client_id", clientId), new KeyValuePair<string, object?>("reason", "expired"));
                    
                    logger.LogWarning(
                        "Client secret expired: ClientId={ClientId}, SecretId={SecretId}, ExpiredAt={ExpiredAt}, Description={Description}",
                        clientId,
                        expiredSecret.Id,
                        expiredSecret.ExpiresAtUtc,
                        expiredSecret.Description);
                    return false;
                }
            }
            
            // No matching secret found
            metrics?.AuthenticationFailure.Add(1, new KeyValuePair<string, object?>("client_id", clientId), new KeyValuePair<string, object?>("reason", "invalid"));
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
        
        var isValid = hasher.Verify(clientSecret, client.ClientSecretHash);
        if (isValid)
        {
            // Record success metric for legacy secret
            metrics?.AuthenticationSuccess.Add(1, 
                new KeyValuePair<string, object?>("client_id", clientId), 
                new KeyValuePair<string, object?>("is_primary", true),
                new KeyValuePair<string, object?>("legacy", true));
        }
        else
        {
            // Record failure metric
            metrics?.AuthenticationFailure.Add(1, 
                new KeyValuePair<string, object?>("client_id", clientId), 
                new KeyValuePair<string, object?>("reason", "invalid_legacy"));
        }
        
        return isValid;
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
            Id = GuidHelper.NewId(),
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
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var secret = await db.ClientSecrets
                .FirstOrDefaultAsync(s => s.Id == secretId, ct)
                .ConfigureAwait(false);

            if (secret == null) return false;
            if (secret.IsPrimary) return true;

            // EF Core InMemory provider doesn't support transactions; tests may treat the
            // TransactionIgnoredWarning as an error. Only use transactions for relational stores.
            if (!db.Database.IsRelational())
            {
                var primarySecretsToClear = await db.ClientSecrets
                    .Where(s => s.ClientId == secret.ClientId && s.Id != secretId && s.IsPrimary)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var other in primarySecretsToClear)
                {
                    other.IsPrimary = false;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                secret.IsPrimary = true;

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Clear IsPrimary flag on all other secrets for this client
            var otherSecrets = await db.ClientSecrets
                .Where(s => s.ClientId == secret.ClientId && s.Id != secretId && s.IsPrimary)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var other in otherSecrets)
            {
                other.IsPrimary = false;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            secret.IsPrimary = true;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
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
