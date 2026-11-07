using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// The server-side OP session (F1). Its id is the <c>sid</c> that the ID token and the logout token carry, so it
/// stays stable for the whole session and on every replica, and the session can be revoked on the server.
/// </summary>
public enum OpSessionStatus
{
    /// <summary>The session exists and may be used.</summary>
    Valid = 0,
    /// <summary>There is no such session (never created, or it was deleted).</summary>
    NotFound = 1,
    /// <summary>The session was revoked (a sign-out, a password change, an administrator).</summary>
    Revoked = 2,
    /// <summary>The session is over: the absolute timeout reached.</summary>
    Expired = 3,
    /// <summary>The session is over: it was not used within the idle timeout.</summary>
    IdleExpired = 4
}

/// <summary>
/// The timeouts of an OP session. A tenant can override them; the platform values apply otherwise.
/// </summary>
public sealed class OpSessionOptions
{
    /// <summary>Turn the server-side session store off (the cookie keeps its current behaviour).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a session survives without a request.</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>How long a session survives in total.</summary>
    public int AbsoluteTimeoutHours { get; set; } = 8;
}

/// <summary>
/// The server-side OP sessions of a tenant (F1).
/// </summary>
public interface IOpSessionService
{
    /// <summary>Creates a session for a sign-in and returns it.</summary>
    Task<OpSession> CreateAsync(
        Guid tenantId,
        Guid userId,
        Guid? userAccountId = null,
        string? acr = null,
        string? amrJson = null,
        string? upstreamIdp = null,
        string? ipHash = null,
        string? userAgent = null,
        DateTimeOffset? authTime = null,
        CancellationToken ct = default);

    /// <summary>The session of the current tenant, or null when there is none.</summary>
    Task<OpSession?> GetAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The sessions of a user (the newest first). Only the sessions of the given tenant are returned.
    /// </summary>
    Task<IReadOnlyList<OpSession>> ListForUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks the session and touches it: a valid session moves its idle deadline. A session that is revoked,
    /// expired or idle is reported with the reason.
    /// </summary>
    Task<OpSessionStatus> CheckAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The state of the session, without touching it: the idle deadline is not moved. The check_session endpoint
    /// polls this, and a poll must not keep an idle session alive.
    /// </summary>
    Task<OpSessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Revokes the session and, when asked, the refresh tokens it produced. Use this for an explicit end of a
    /// session (the account page, an administrator): it always revokes the tokens, whatever the client's
    /// <see cref="Client.RevokeRefreshOnLogout"/> policy says. Returns the number of revoked refresh tokens; zero
    /// when there was no such session or when the session had no refresh token.
    /// </summary>
    Task<int> RevokeAsync(Guid sessionId, string reason, bool revokeTokens, CancellationToken ct = default);

    /// <summary>
    /// Revokes the session of a user sign-out. The refresh tokens it produced are revoked too, except the tokens
    /// of clients that opted out with <see cref="Client.RevokeRefreshOnLogout"/>; those keep refreshing because
    /// this was a sign-out, not a security revocation. Returns the number of revoked refresh tokens.
    /// </summary>
    Task<int> RevokeForLogoutAsync(Guid sessionId, string reason, CancellationToken ct = default);

    /// <summary>
    /// True when the session was revoked by a sign-out and <paramref name="clientId"/> opted out of the refresh
    /// token revocation. The refresh grant uses this to keep the token of that client usable after the sign-out.
    /// A session ended by expiry or by a security revocation never answers true.
    /// </summary>
    Task<bool> AllowsRefreshAfterLogoutAsync(Guid sessionId, string clientId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every session of a user except one (the current session) and the refresh tokens they produced.
    /// Returns the number of revoked sessions.
    /// </summary>
    Task<int> RevokeAllForUserAsync(
        Guid tenantId,
        Guid userId,
        Guid? exceptSessionId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes every session of the per-tenant users of an account, in every tenant the account belongs to, and
    /// the refresh tokens they produced. This is the counterpart of the security-stamp rotation: a credential
    /// change must end the sessions of the account, not only the cookies that carry the old stamp.
    /// Returns the number of revoked sessions.
    /// </summary>
    Task<int> RevokeForAccountAsync(Guid accountId, string reason, CancellationToken ct = default);

    /// <summary>Records that a client received a token in this session.</summary>
    Task RegisterClientAsync(Guid sessionId, string clientId, CancellationToken ct = default);

    /// <summary>The clients that received a token in this session.</summary>
    Task<IReadOnlyList<string>> GetClientIdsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The clients that received a token in the given sessions. A session without a client is not in the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetClientIdsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken ct = default);
}

internal sealed class OpSessionService(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    ITenantSettingsService tenantSettings,
    IOptions<OpSessionOptions> options,
    ILogger<OpSessionService> logger) : IOpSessionService
{
    /// <summary>How stale the "last seen" of a session may get before it is written again.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<OpSession> CreateAsync(
        Guid tenantId,
        Guid userId,
        Guid? userAccountId = null,
        string? acr = null,
        string? amrJson = null,
        string? upstreamIdp = null,
        string? ipHash = null,
        string? userAgent = null,
        DateTimeOffset? authTime = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var (idle, absolute) = await ResolveTimeoutsAsync(ct).ConfigureAwait(false);

        var session = new OpSession
        {
            TenantId = tenantId,
            UserId = userId,
            UserAccountId = userAccountId,
            AuthTime = authTime ?? now,
            LastSeenAt = now,
            ExpiresAt = (authTime ?? now) + absolute,
            IdleExpiresAt = now + idle,
            Acr = acr,
            AmrJson = amrJson,
            UpstreamIdp = upstreamIdp,
            IpHash = ipHash,
            UserAgent = userAgent
        };

        db.OpSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Created the OP session {SessionId} for user {UserId} in tenant {TenantId}",
            session.Id, userId, tenantId);

        return session;
    }

    public async Task<OpSession?> GetAsync(Guid sessionId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return null;
        }

        return await db.OpSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OpSession>> ListForUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct = default)
    {
        return await db.OpSessions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.UserId == userId)
            .OrderByDescending(s => s.AuthTime)
            .ThenByDescending(s => s.LastSeenAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<OpSessionStatus> CheckAsync(Guid sessionId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return OpSessionStatus.NotFound;
        }

        var session = await db.OpSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            return OpSessionStatus.NotFound;
        }

        var status = Evaluate(session);
        if (status != OpSessionStatus.Valid)
        {
            return status;
        }

        // The session is used: move the idle deadline. The write is throttled, so a busy session does not write on
        // every request.
        var (idle, _) = await ResolveTimeoutsAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (now - session.LastSeenAt >= TouchInterval)
        {
            session.LastSeenAt = now;
            session.IdleExpiresAt = now + idle;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return OpSessionStatus.Valid;
    }

    public async Task<OpSessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return OpSessionStatus.NotFound;
        }

        var session = await db.OpSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);

        return session is null ? OpSessionStatus.NotFound : Evaluate(session);
    }

    /// <summary>
    /// The state of the session, from its columns.
    /// </summary>
    private static OpSessionStatus Evaluate(OpSession session)
    {
        var now = DateTimeOffset.UtcNow;
        if (session.RevokedAt is not null)
        {
            return OpSessionStatus.Revoked;
        }

        if (session.ExpiresAt <= now)
        {
            return OpSessionStatus.Expired;
        }

        if (session.IdleExpiresAt <= now)
        {
            return OpSessionStatus.IdleExpired;
        }

        return OpSessionStatus.Valid;
    }

    public async Task<int> RevokeAsync(Guid sessionId, string reason, bool revokeTokens, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return 0;
        }

        var session = await db.OpSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            return 0;
        }

        // A session that was already ended by a sign-out may still hold the tokens of clients that opted out of
        // the logout revocation (RevokeForLogoutAsync). An explicit revocation is a security action: it ends
        // those too, even though the session row itself is already revoked.
        if (session.RevokedAt is not null)
        {
            // The session was a sign-out; an explicit revocation removes the "keep refreshing" exception as well.
            session.RevokedByLogout = false;
            var survivingTokens = revokeTokens
                ? await RevokeSessionTokensAsync([session.Id], honorClientLogoutPolicy: false, ct).ConfigureAwait(false)
                : 0;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return survivingTokens;
        }

        session.RevokedAt = DateTimeOffset.UtcNow;
        session.RevokedReason = reason.Length > 200 ? reason[..200] : reason;
        session.RevokedByLogout = false;

        // A session that the user ends must end the tokens it produced: the refresh tokens of the session are
        // revoked in the same change, so the relying parties see the sign-out on their next refresh.
        var revokedTokens = revokeTokens
            ? await RevokeSessionTokensAsync([session.Id], honorClientLogoutPolicy: false, ct).ConfigureAwait(false)
            : 0;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked the OP session {SessionId} in tenant {TenantId}: {Reason} ({RevokedTokens} tokens)",
            sessionId, tenantId, reason, revokedTokens);

        return revokedTokens;
    }

    public async Task<int> RevokeForLogoutAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return 0;
        }

        var session = await db.OpSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null)
        {
            return 0;
        }

        session.RevokedAt = DateTimeOffset.UtcNow;
        session.RevokedReason = reason.Length > 200 ? reason[..200] : reason;
        session.RevokedByLogout = true;

        var revokedTokens = await RevokeSessionTokensAsync(
            [session.Id],
            honorClientLogoutPolicy: true,
            ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked the OP session {SessionId} in tenant {TenantId} on sign-out: {Reason} ({RevokedTokens} tokens)",
            sessionId, tenantId, reason, revokedTokens);

        return revokedTokens;
    }

    public async Task<bool> AllowsRefreshAfterLogoutAsync(
        Guid sessionId,
        string clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return false;
        }

        var revokedByLogout = await db.OpSessions
            .AsNoTracking()
            .AnyAsync(
                s => s.Id == sessionId
                     && s.TenantId == tenantId.Value
                     && s.RevokedAt != null
                     && s.RevokedByLogout,
                ct)
            .ConfigureAwait(false);
        if (!revokedByLogout)
        {
            return false;
        }

        return await db.Clients
            .AsNoTracking()
            .Where(c => c.ClientId == clientId && c.TenantId == tenantId.Value)
            .Select(c => (bool?)c.RevokeRefreshOnLogout)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) == false;
    }

    public async Task<int> RevokeAllForUserAsync(
        Guid tenantId,
        Guid userId,
        Guid? exceptSessionId,
        string reason,
        CancellationToken ct = default)
    {
        // Include the sessions that a sign-out already ended: their opted-out refresh tokens are still live, and
        // this is an explicit (or security) end of every other session. A session revoked for another reason has
        // no live token.
        var sessions = await db.OpSessions
            .Where(s => s.TenantId == tenantId && s.UserId == userId && (s.RevokedAt == null || s.RevokedByLogout))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (exceptSessionId is { } keep)
        {
            sessions.RemoveAll(s => s.Id == keep);
        }

        if (sessions.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var truncatedReason = reason.Length > 200 ? reason[..200] : reason;
        var revokedSessions = 0;
        foreach (var session in sessions)
        {
            // An already-ended sign-out session may still hold an opted-out token. Remove the exception as well,
            // so the session check cannot revive it after this explicit revocation.
            session.RevokedByLogout = false;
            if (session.RevokedAt is not null)
            {
                continue;
            }

            session.RevokedAt = now;
            session.RevokedReason = truncatedReason;
            revokedSessions++;
        }

        // The tokens of every session in the set are revoked, including the tokens a client opted to keep after
        // its sign-out: revoking all sessions must not leave one of them alive.
        var sessionIds = sessions.Select(s => s.Id).ToArray();
        await RevokeSessionTokensAsync(sessionIds, honorClientLogoutPolicy: false, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked {Count} OP sessions of user {UserId} in tenant {TenantId}: {Reason}",
            revokedSessions, userId, tenantId, reason);

        return revokedSessions;
    }

    public async Task<int> RevokeForAccountAsync(Guid accountId, string reason, CancellationToken ct = default)
    {
        // The account spans several tenants, so the tenant filter is bypassed here and the membership is the
        // authority. The same resolution as UserAccountService.RevokeTokensForAccountAsync: the explicit link,
        // else the legacy home-user id, else the legacy rows of the account's tenants that resolve to it by email.
        var tenantIds = await db.UserTenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserAccountId == accountId)
            .Select(m => m.TenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var normalizedEmail = await db.UserAccounts
            .IgnoreQueryFilters()
            .Where(a => a.Id == accountId)
            .Select(a => a.NormalizedEmail)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var userIds = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.UserAccountId == accountId
                        || u.Id == accountId
                        || (u.UserAccountId == null
                            && u.Id != accountId
                            && tenantIds.Contains(u.TenantId)
                            && normalizedEmail != null
                            && u.NormalizedEmail == normalizedEmail))
            .Select(u => u.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The sessions of the account are found by their user, and by the account they carry: a session that was
        // created before the account link existed still carries the account id. Include the sessions that a
        // sign-out already ended: their opted-out refresh tokens are still live and a credential change must end
        // them too. A session revoked for another reason has no live token.
        var sessions = await db.OpSessions
            .IgnoreQueryFilters()
            .Where(s => (s.UserAccountId == accountId || userIds.Contains(s.UserId))
                        && (s.RevokedAt == null || s.RevokedByLogout))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var truncatedReason = reason.Length > 200 ? reason[..200] : reason;
        var revokedSessions = 0;
        foreach (var session in sessions)
        {
            // A sign-out session may still hold the token of a client that opted out; a credential change removes
            // the exception and the token below.
            session.RevokedByLogout = false;
            if (session.RevokedAt is not null)
            {
                continue;
            }

            session.RevokedAt = now;
            session.RevokedReason = truncatedReason;
            revokedSessions++;
        }

        var sessionIds = sessions.Select(s => s.Id).ToArray();
        await RevokeSessionTokensAsync(sessionIds, honorClientLogoutPolicy: false, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked {Count} OP sessions of account {AccountId}: {Reason}",
            revokedSessions, accountId, reason);

        return revokedSessions;
    }

    /// <summary>
    /// Revokes the refresh tokens of the given sessions. The update is set-based, so it is one statement per
    /// session set (the index on Tokens.SessionId finds the rows).
    /// <para>
    /// When <paramref name="honorClientLogoutPolicy"/> is true, the tokens of clients whose
    /// <see cref="Client.RevokeRefreshOnLogout"/> is off are left alone. The query excludes them in the database,
    /// so the policy does not require loading the token rows.
    /// </para>
    /// </summary>
    private async Task<int> RevokeSessionTokensAsync(
        Guid[] sessionIds,
        bool honorClientLogoutPolicy,
        CancellationToken ct)
    {
        if (sessionIds.Length == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        var query = db.Tokens
            .IgnoreQueryFilters()
            .Where(t => t.SessionId != null && sessionIds.Contains(t.SessionId.Value) && t.Type == "refresh" && t.RevokedAt == null);

        if (honorClientLogoutPolicy)
        {
            // The safe default is "revoke": only a client that explicitly opted out is excluded.
            var optedOutClientIds = db.Clients
                .IgnoreQueryFilters()
                .Where(c => !c.RevokeRefreshOnLogout)
                .Select(c => c.ClientId);
            query = query.Where(t => !optedOutClientIds.Contains(t.ClientId));
        }

        if (!db.Database.IsRelational())
        {
            // The in-memory provider (unit tests) has no set-based update.
            var tokens = await query.ToListAsync(ct).ConfigureAwait(false);
            foreach (var token in tokens)
            {
                token.RevokedAt = now;
            }

            return tokens.Count;
        }

        return await query
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct)
            .ConfigureAwait(false);
    }

    public async Task RegisterClientAsync(Guid sessionId, string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return;
        }

        var exists = await db.OpSessionClients
            .AnyAsync(c => c.SessionId == sessionId && c.ClientId == clientId, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.OpSessionClients.Add(new OpSessionClient
        {
            TenantId = tenantId.Value,
            SessionId = sessionId,
            ClientId = clientId,
            FirstSeenAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two replicas registered the same client at the same time: the unique index settled it.
            logger.LogDebug("The client {ClientId} was already registered for the session {SessionId}", clientId, sessionId);
        }
    }

    public async Task<IReadOnlyList<string>> GetClientIdsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return Array.Empty<string>();
        }

        return await db.OpSessionClients
            .AsNoTracking()
            .Where(c => c.SessionId == sessionId && c.TenantId == tenantId.Value)
            .Select(c => c.ClientId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetClientIdsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken ct = default)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var rows = await db.OpSessionClients
            .AsNoTracking()
            .Where(c => sessionIds.Contains(c.SessionId) && c.TenantId == tenantId.Value)
            .Select(c => new { c.SessionId, c.ClientId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.SessionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.ClientId).Distinct().ToList());
    }

    /// <summary>
    /// The idle and the absolute timeout of the current tenant (the tenant settings override the platform
    /// values).
    /// </summary>
    private async Task<(TimeSpan Idle, TimeSpan Absolute)> ResolveTimeoutsAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var idleMinutes = opts.IdleTimeoutMinutes;
        var absoluteHours = opts.AbsoluteTimeoutHours;

        try
        {
            var settings = await tenantSettings.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
            var tenant = settings.Auth?.OpSession;
            if (tenant?.IdleTimeoutMinutes is > 0)
            {
                idleMinutes = tenant.IdleTimeoutMinutes.Value;
            }

            if (tenant?.AbsoluteTimeoutHours is > 0)
            {
                absoluteHours = tenant.AbsoluteTimeoutHours.Value;
            }
        }
        catch (Exception ex)
        {
            // The platform values apply when the tenant settings cannot be read.
            logger.LogWarning(ex, "Could not read the tenant session settings; the platform timeouts apply");
        }

        return (TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromHours(absoluteHours));
    }
}
