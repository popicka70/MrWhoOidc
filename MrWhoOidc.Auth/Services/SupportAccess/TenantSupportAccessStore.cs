using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.SupportAccess;

/// <summary>
/// EF Core-backed implementation of the Tenant Support Access session store.
/// Provides durable persistence with optimistic concurrency control via ConcurrencyToken.
/// </summary>
public sealed class TenantSupportAccessStore(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    ILogger<TenantSupportAccessStore> logger) : ITenantSupportAccessStore
{
    /// <summary>
    /// Retrieves a session by ID, verifying tenant association.
    /// Returns null if not found or tenant does not match.
    /// </summary>
    public async Task<TenantSupportAccessSession?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default)
    {
        var session = await db.TenantSupportAccessSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// Persists a new support access session.
    /// </summary>
    public async Task CreateAsync(TenantSupportAccessSession session, CancellationToken ct = default)
    {
        db.TenantSupportAccessSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates an existing session with optimistic concurrency control.
    /// Reads the current persisted value, verifies the ConcurrencyToken matches,
    /// applies the update, and generates a new ConcurrencyToken.
    /// Throws if the session is not found or the concurrency token does not match.
    /// </summary>
    public async Task UpdateAsync(TenantSupportAccessSession session, CancellationToken ct = default)
    {
        var existing = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            throw new InvalidOperationException("Session not found");
        }

        if (existing.ConcurrencyToken != session.ConcurrencyToken)
        {
            throw new InvalidOperationException("Concurrency token mismatch");
        }

        // Apply all updates from the provided session
        existing.PlatformAdminUserAccountId = session.PlatformAdminUserAccountId;
        existing.TenantId = session.TenantId;
        existing.Mode = session.Mode;
        existing.Status = session.Status;
        existing.Reason = session.Reason;
        existing.TicketReference = session.TicketReference;
        existing.CreatedAt = session.CreatedAt;
        existing.ExpiresAt = session.ExpiresAt;
        existing.EndedAt = session.EndedAt;
        existing.RevokedAt = session.RevokedAt;
        existing.RevokedByUserAccountId = session.RevokedByUserAccountId;
        existing.RevocationReason = session.RevocationReason;
        existing.LastSeenAt = session.LastSeenAt;
        existing.CreatedFromIpHash = session.CreatedFromIpHash;
        existing.UserAgentHash = session.UserAgentHash;
        existing.ConcurrencyToken = GuidHelper.NewId(); // Update concurrency token

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes an active session. Loads the session by ID, sets status to Revoked,
    /// records revocation metadata, and updates the concurrency token.
    /// Throws if the session is not found or is not in a revocable state.
    /// </summary>
    public async Task RevokeAsync(Guid sessionId, Guid revokerAccountId, string reason, CancellationToken ct = default)
    {
        var session = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);

        if (session == null)
        {
            throw new InvalidOperationException("Session not found");
        }

        if (session.Status != SupportAccessStatus.Active)
        {
            throw new InvalidOperationException("Session is not active and cannot be revoked");
        }

        session.Status = SupportAccessStatus.Revoked;
        session.RevokedAt = DateTimeOffset.UtcNow;
        session.RevokedByUserAccountId = revokerAccountId;
        session.RevocationReason = reason;
        session.ConcurrencyToken = GuidHelper.NewId();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves all active (Status == Active and not expired) sessions for a given tenant.
    /// Filters by tenant and active status, then checks expiration.
    /// </summary>
    public async Task<List<TenantSupportAccessSession>> GetActiveSessionsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var sessions = await db.TenantSupportAccessSessions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                      && s.Status == SupportAccessStatus.Active
                      && s.ExpiresAt > now)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sessions;
    }
}
