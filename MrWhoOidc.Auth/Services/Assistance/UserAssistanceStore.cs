using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Assistance;

/// <summary>
/// Persistence for consent-based user assistance requests and sessions. Every method opens an explicit
/// tenant scope because the helper's request may target the tenant they are currently working in, and the
/// requester/helper checks must see the durable row regardless of the request's tenant filter.
/// </summary>
public interface IUserAssistanceStore
{
    Task<UserAssistanceSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserAssistanceSession?> GetPendingByInvitationHashAsync(string tokenHash, CancellationToken ct = default);
    Task<UserAssistanceSession?> GetPendingForRequesterAsync(Guid requesterUserId, CancellationToken ct = default);
    Task<UserAssistanceSession?> GetActiveForRequesterAsync(Guid requesterUserId, CancellationToken ct = default);
    Task<UserAssistanceSession?> GetActiveForHelperAsync(Guid helperUserId, Guid helperUserAccountId, CancellationToken ct = default);
    Task<UserAssistanceSession?> GetActiveByTargetSessionAsync(Guid targetOpSessionId, CancellationToken ct = default);
    Task<List<UserAssistanceSession>> ListForRequesterAsync(Guid requesterUserId, CancellationToken ct = default);
    Task<List<UserAssistanceSession>> ListForHelperAsync(Guid helperUserId, CancellationToken ct = default);
    Task<List<UserAssistanceSession>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task CreateAsync(UserAssistanceSession session, CancellationToken ct = default);
    Task UpdateAsync(UserAssistanceSession session, CancellationToken ct = default);
    Task<bool> EndAsync(
        Guid sessionId,
        UserAssistanceStatus status,
        DateTimeOffset endedAt,
        Guid? endedByUserAccountId,
        string? reason,
        CancellationToken ct = default);
    Task<int> RevokeTargetSessionAsync(Guid targetOpSessionId, string reason, CancellationToken ct = default);
}

public sealed class UserAssistanceStore(AuthDbContext db) : IUserAssistanceStore
{
    public async Task<UserAssistanceSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceSession?> GetPendingByInvitationHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.InvitationTokenHash == tokenHash
                                      && s.Status == UserAssistanceStatus.Pending
                                      && s.ExpiresAt > now, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceSession?> GetPendingForRequesterAsync(Guid requesterUserId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.RequesterUserId == requesterUserId
                        && s.Status == UserAssistanceStatus.Pending
                        && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceSession?> GetActiveForRequesterAsync(Guid requesterUserId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.RequesterUserId == requesterUserId
                        && s.Status == UserAssistanceStatus.Active
                        && s.SessionExpiresAt > now)
            .OrderByDescending(s => s.AcceptedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceSession?> GetActiveForHelperAsync(
        Guid helperUserId,
        Guid helperUserAccountId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.Status == UserAssistanceStatus.Active
                        && s.SessionExpiresAt > now
                        && (s.HelperUserId == helperUserId || s.HelperUserAccountId == helperUserAccountId))
            .OrderByDescending(s => s.AcceptedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceSession?> GetActiveByTargetSessionAsync(Guid targetOpSessionId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TargetOpSessionId == targetOpSessionId
                                      && s.Status == UserAssistanceStatus.Active
                                      && s.SessionExpiresAt > now, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<UserAssistanceSession>> ListForRequesterAsync(Guid requesterUserId, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.RequesterUserId == requesterUserId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<UserAssistanceSession>> ListForHelperAsync(Guid helperUserId, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.HelperUserId == helperUserId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<UserAssistanceSession>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(200)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task CreateAsync(UserAssistanceSession session, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        db.UserAssistanceSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(UserAssistanceSession session, CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        var existing = await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        if (existing.ConcurrencyToken != session.ConcurrencyToken)
        {
            throw new InvalidOperationException("Assistance session concurrency token mismatch.");
        }

        existing.RequesterUserId = session.RequesterUserId;
        existing.RequesterUserAccountId = session.RequesterUserAccountId;
        existing.RequesterUsername = session.RequesterUsername;
        existing.RequesterDisplayName = session.RequesterDisplayName;
        existing.HelperUserId = session.HelperUserId;
        existing.HelperUserAccountId = session.HelperUserAccountId;
        existing.HelperUsername = session.HelperUsername;
        existing.HelperEmail = session.HelperEmail;
        existing.Mode = session.Mode;
        existing.RightsMode = session.RightsMode;
        existing.Status = session.Status;
        existing.Reason = session.Reason;
        existing.TicketReference = session.TicketReference;
        existing.RequestedDurationMinutes = session.RequestedDurationMinutes;
        existing.CreatedAt = session.CreatedAt;
        existing.ExpiresAt = session.ExpiresAt;
        existing.AcceptedAt = session.AcceptedAt;
        existing.SessionExpiresAt = session.SessionExpiresAt;
        existing.EndedAt = session.EndedAt;
        existing.EndedByUserAccountId = session.EndedByUserAccountId;
        existing.EndReason = session.EndReason;
        existing.TargetOpSessionId = session.TargetOpSessionId;
        existing.TargetSecurityStamp = session.TargetSecurityStamp;
        existing.ActorAcr = session.ActorAcr;
        existing.ActorAmrJson = session.ActorAmrJson;
        existing.ActorIdp = session.ActorIdp;
        existing.InvitationTokenHash = session.InvitationTokenHash;
        existing.CreatedFromIpHash = session.CreatedFromIpHash;
        existing.UserAgentHash = session.UserAgentHash;
        existing.LastSeenAt = session.LastSeenAt;
        existing.EffectiveRoleIdsJson = session.EffectiveRoleIdsJson;
        existing.ConcurrencyToken = GuidHelper.NewId();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        session.ConcurrencyToken = existing.ConcurrencyToken;
    }

    public async Task<bool> EndAsync(
        Guid sessionId,
        UserAssistanceStatus status,
        DateTimeOffset endedAt,
        Guid? endedByUserAccountId,
        string? reason,
        CancellationToken ct = default)
    {
        using var scope = TenantFilterScope.BeginCrossTenantScope();
        var session = await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || session.Status is not (UserAssistanceStatus.Active or UserAssistanceStatus.Pending))
        {
            return false;
        }

        session.Status = status;
        session.EndedAt = endedAt;
        session.EndedByUserAccountId = endedByUserAccountId;
        session.EndReason = reason;
        session.InvitationTokenHash = null;
        session.ConcurrencyToken = GuidHelper.NewId();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (session.TargetOpSessionId is { } targetOpSessionId)
        {
            await RevokeTargetSessionCoreAsync(targetOpSessionId, "Assistance session ended", endedAt, ct)
                .ConfigureAwait(false);
        }

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
}
