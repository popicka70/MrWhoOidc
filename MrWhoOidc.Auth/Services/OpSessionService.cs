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
    /// Checks the session and touches it: a valid session moves its idle deadline. A session that is revoked,
    /// expired or idle is reported with the reason.
    /// </summary>
    Task<OpSessionStatus> CheckAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Revokes the session. Returns false when there was no such session.</summary>
    Task<bool> RevokeAsync(Guid sessionId, string reason, CancellationToken ct = default);

    /// <summary>Records that a client received a token in this session.</summary>
    Task RegisterClientAsync(Guid sessionId, string clientId, CancellationToken ct = default);

    /// <summary>The clients that received a token in this session.</summary>
    Task<IReadOnlyList<string>> GetClientIdsAsync(Guid sessionId, CancellationToken ct = default);
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

        // The session is used: move the idle deadline. The write is throttled, so a busy session does not write on
        // every request.
        var (idle, _) = await ResolveTimeoutsAsync(ct).ConfigureAwait(false);
        if (now - session.LastSeenAt >= TouchInterval)
        {
            session.LastSeenAt = now;
            session.IdleExpiresAt = now + idle;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return OpSessionStatus.Valid;
    }

    public async Task<bool> RevokeAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return false;
        }

        var session = await db.OpSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId.Value, ct)
            .ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null)
        {
            return false;
        }

        session.RevokedAt = DateTimeOffset.UtcNow;
        session.RevokedReason = reason.Length > 200 ? reason[..200] : reason;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked the OP session {SessionId} in tenant {TenantId}: {Reason}",
            sessionId, tenantId, reason);

        return true;
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
