using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Impersonation;

/// <summary>
/// EF Core implementation of <see cref="IUserImpersonationStore"/>. Every method that can touch another
/// tenant's rows opens an explicit cross-tenant scope, because the actor is a platform administrator and the
/// session's tenant is not necessarily the request's tenant.
/// </summary>
public sealed class UserImpersonationStore(AuthDbContext db) : IUserImpersonationStore
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<UserImpersonationSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserImpersonationSession?> GetActiveForActorAsync(Guid actorUserAccountId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.ActorUserAccountId == actorUserAccountId
                        && s.Status == UserImpersonationStatus.Active
                        && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<UserImpersonationSession?> GetActiveByTargetSessionAsync(Guid targetOpSessionId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TargetOpSessionId == targetOpSessionId
                        && s.Status == UserImpersonationStatus.Active
                        && s.ExpiresAt > now)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task CreateAsync(UserImpersonationSession session, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        db.UserImpersonationSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(UserImpersonationSession session, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        var existing = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        if (existing.ConcurrencyToken != session.ConcurrencyToken)
        {
            throw new InvalidOperationException("Impersonation session concurrency token mismatch.");
        }

        existing.ActorUserAccountId = session.ActorUserAccountId;
        existing.ActorUserId = session.ActorUserId;
        existing.ActorUsername = session.ActorUsername;
        existing.TenantId = session.TenantId;
        existing.TenantName = session.TenantName;
        existing.TenantSlug = session.TenantSlug;
        existing.TargetUserId = session.TargetUserId;
        existing.TargetUserAccountId = session.TargetUserAccountId;
        existing.TargetUsername = session.TargetUsername;
        existing.TargetDisplayName = session.TargetDisplayName;
        existing.TargetSecurityStamp = session.TargetSecurityStamp;
        existing.TargetOpSessionId = session.TargetOpSessionId;
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
        existing.ActorAcr = session.ActorAcr;
        existing.ActorAmrJson = session.ActorAmrJson;
        existing.ActorIdp = session.ActorIdp;
        existing.ConcurrencyToken = GuidHelper.NewId();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        session.ConcurrencyToken = existing.ConcurrencyToken;
    }

    public async Task<bool> EndAsync(
        Guid sessionId,
        UserImpersonationStatus status,
        DateTimeOffset endedAt,
        Guid? revokedByUserAccountId,
        string? revocationReason,
        CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        var session = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || session.Status != UserImpersonationStatus.Active)
        {
            return false;
        }

        session.Status = status;
        session.EndedAt = endedAt;
        if (status == UserImpersonationStatus.Revoked)
        {
            session.RevokedAt = endedAt;
            session.RevokedByUserAccountId = revokedByUserAccountId;
            session.RevocationReason = revocationReason;
        }

        session.ConcurrencyToken = GuidHelper.NewId();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RevokeTargetSessionCoreAsync(session.TargetOpSessionId, "Impersonation session ended", endedAt, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<int> RevokeTargetSessionAsync(Guid targetOpSessionId, string reason, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        return await RevokeTargetSessionCoreAsync(targetOpSessionId, reason, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    private async Task<int> RevokeTargetSessionCoreAsync(
        Guid targetOpSessionId,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var session = await db.OpSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == targetOpSessionId, ct)
            .ConfigureAwait(false);

        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason.Length > 200 ? reason[..200] : reason;
            session.RevokedByLogout = false;
        }

        var tokens = await db.Tokens
            .IgnoreQueryFilters()
            .Where(t => t.SessionId == targetOpSessionId && t.RevokedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tokens.Count;
    }

    public async Task<List<UserImpersonationSession>> ListAsync(
        string? actorUsername,
        string? targetUsername,
        string? tenantSlug,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await Filter(actorUsername, targetUsername, tenantSlug, start, end)
            .IgnoreQueryFilters()
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(
        string? actorUsername,
        string? targetUsername,
        string? tenantSlug,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await Filter(actorUsername, targetUsername, tenantSlug, start, end)
            .IgnoreQueryFilters()
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    private IQueryable<UserImpersonationSession> Filter(
        string? actorUsername,
        string? targetUsername,
        string? tenantSlug,
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        var query = db.UserImpersonationSessions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(actorUsername))
        {
            var term = actorUsername.Trim();
            query = query.Where(s => s.ActorUsername.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(targetUsername))
        {
            var term = targetUsername.Trim();
            query = query.Where(s => s.TargetUsername.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            var term = tenantSlug.Trim();
            query = query.Where(s => s.TenantSlug.Contains(term));
        }

        if (start is { } from)
        {
            query = query.Where(s => s.CreatedAt >= from);
        }

        if (end is { } to)
        {
            query = query.Where(s => s.CreatedAt < to);
        }

        return query;
    }
}
