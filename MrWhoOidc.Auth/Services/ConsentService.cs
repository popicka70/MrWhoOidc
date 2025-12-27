using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IConsentService
{
    /// <summary>
    /// Checks if a user has already granted consent for the requested scopes for a specific client.
    /// </summary>
    Task<bool> HasConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default);

    /// <summary>
    /// Grants consent for the requested scopes for a specific user and client.
    /// Uses a transaction and execution strategy to ensure consistency under high concurrency.
    /// </summary>
    Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default);
}

internal sealed class ConsentService(AuthDbContext db, ITenantAccessor tenantAccessor) : IConsentService
{
    public async Task<bool> HasConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
    {
        var query = db.Consents.AsNoTracking()
            .Where(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        var consent = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (consent is null) return false;

        // If no scopes requested beyond openid, treat as consented
        var requested = scopes.Where(s => !string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return true;

        var granted = System.Text.Json.JsonSerializer.Deserialize<string[]>(consent.ScopesJson) ?? Array.Empty<string>();
        var grantedSet = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
        // Ensure all requested scopes are already granted
        return requested.All(s => grantedSet.Contains(s));
    }

    public async Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var query = db.Consents.Where(c => c.UserId == userId && c.ClientId == clientId);

                // Filter by tenant if tenant context is available
                if (tenantAccessor.CurrentTenant != null)
                {
                    query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
                }

                var existing = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var requested = scopes.Where(s => !string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var scopesJson = System.Text.Json.JsonSerializer.Serialize(requested.Distinct(StringComparer.OrdinalIgnoreCase));
                    var consent = new Consent
                    {
                        UserId = userId,
                        ClientId = clientId,
                        ScopesJson = scopesJson,
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    // Set TenantId if tenant context is available
                    if (tenantAccessor.CurrentTenant != null)
                    {
                        consent.TenantId = tenantAccessor.CurrentTenant.TenantId;
                    }

                    db.Consents.Add(consent);
                }
                else
                {
                    var current = System.Text.Json.JsonSerializer.Deserialize<string[]>(existing.ScopesJson) ?? Array.Empty<string>();
                    var merged = current.Concat(requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    existing.ScopesJson = System.Text.Json.JsonSerializer.Serialize(merged);
                    existing.RevokedAt = null;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }
}
