using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Impersonation;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Starts, validates and ends real user impersonation sessions.
/// <para>
/// The platform administrator remains the accountable actor. The durable session records the actor and the
/// subject separately, and the request pipeline overlays the target principal only while the session is valid.
/// </para>
/// </summary>
public interface IUserImpersonationService
{
    /// <summary>Session key that points at the durable impersonation session.</summary>
    string SessionIdKey { get; }

    /// <summary>Starts impersonation of one active user. The caller must be a platform administrator.</summary>
    Task<UserImpersonationStartResult> StartAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        Guid targetUserId,
        string reason,
        int? expiryMinutes = null,
        string? ticketReference = null);

    /// <summary>Ends the current impersonation session (graceful stop).</summary>
    Task<bool> StopAsync(HttpContext context);

    /// <summary>
    /// Revokes an active impersonation session by id. Used by the history page for security response.
    /// </summary>
    Task<bool> RevokeAsync(ClaimsPrincipal revoker, Guid sessionId, string reason);

    /// <summary>Display information for the active session, or null.</summary>
    Task<UserImpersonationInfo?> GetInfoAsync(HttpContext context);

    /// <summary>The active session id referenced by the ASP.NET session, if any.</summary>
    Guid? GetSessionId(HttpContext context);

    /// <summary>Builds the overlay principal for a target session. Returns null when validation fails.</summary>
    Task<UserImpersonationOverlay?> ResolveOverlayAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        CancellationToken ct = default);
}

/// <summary>Result of starting an impersonation session.</summary>
public sealed class UserImpersonationStartResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid SessionId { get; init; }
    public Guid TenantId { get; init; }
    public string TenantSlug { get; init; } = string.Empty;
    public string TenantName { get; init; } = string.Empty;
    public string TargetUsername { get; init; } = string.Empty;
    public string? TargetDisplayName { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Validated overlay data for one request.</summary>
public sealed class UserImpersonationOverlay
{
    public required UserImpersonationSession Session { get; init; }
    public required User TargetUser { get; init; }
    public required UserAccount TargetAccount { get; init; }
    public required ClaimsPrincipal Principal { get; init; }
}

/// <summary>Display model for the impersonation banner and start page.</summary>
public sealed class UserImpersonationInfo
{
    public Guid SessionId { get; init; }
    public Guid TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string TenantSlug { get; init; } = string.Empty;
    public string ActorUsername { get; init; } = string.Empty;
    public Guid ActorUserAccountId { get; init; }
    public Guid TargetUserId { get; init; }
    public Guid TargetUserAccountId { get; init; }
    public string TargetUsername { get; init; } = string.Empty;
    public string? TargetDisplayName { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string? TicketReference { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public UserImpersonationStatus Status { get; init; }

    public string RemainingTime
    {
        get
        {
            if (Status != UserImpersonationStatus.Active)
            {
                return "Ended";
            }

            var remaining = ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0)
            {
                return "Expired";
            }

            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
        }
    }
}

/// <summary>Implementation of <see cref="IUserImpersonationService"/>.</summary>
public sealed class UserImpersonationService(
    AuthDbContext db,
    IAuthorizationService authorizationService,
    IUserImpersonationStore store,
    IOpSessionService opSessions,
    IUserAccountService userAccounts,
    IOptions<AuthOptions> authOptions,
    IAuditSink audit,
    IUserImpersonationMetrics metrics,
    ILogger<UserImpersonationService> logger) : IUserImpersonationService
{
    public const string SessionKeyName = "UserImpersonationSessionId";
    private const int DefaultExpiryMinutes = 15;
    private const string ImpersonationAcr = "urn:mrwho:acr:impersonation";
    private const string ActorAccountClaim = "urn:mrwho:impersonator-user-account-id";
    private const string ActorUserClaim = "urn:mrwho:impersonator-user-id";
    private const string SessionClaim = "urn:mrwho:impersonation-session-id";

    public string SessionIdKey => SessionKeyName;

    public async Task<UserImpersonationStartResult> StartAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        Guid targetUserId,
        string reason,
        int? expiryMinutes = null,
        string? ticketReference = null)
    {
        static UserImpersonationStartResult Fail(string error)
            => new() { Success = false, Error = error };

        if (!authOptions.Value.EnableUserImpersonation)
        {
            return Fail("User impersonation is disabled.");
        }

        if (!(await authorizationService.AuthorizeAsync(actor, "platform-admin")).Succeeded)
        {
            return Fail("Only platform administrators can impersonate a user.");
        }

        if (targetUserId == Guid.Empty)
        {
            return Fail("Select a user to impersonate.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Fail("A reason is required.");
        }

        reason = reason.Trim();
        if (reason.Length > 500)
        {
            return Fail("The reason must be at most 500 characters.");
        }

        ticketReference = string.IsNullOrWhiteSpace(ticketReference) ? null : ticketReference.Trim();
        if (ticketReference is { Length: > 100 })
        {
            return Fail("The ticket reference must be at most 100 characters.");
        }

        var actorUserId = ParseGuid(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (actorUserId is null)
        {
            return Fail("The administrator identity could not be resolved.");
        }

        var actorAccountId = ParseGuid(actor.FindFirst(UserClaimTypes.UserAccountId)?.Value)
            ?? await ResolveActorAccountIdAsync(actorUserId.Value, context.RequestAborted);
        var actorAccount = actorAccountId is null
            ? null
            : await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == actorAccountId.Value, context.RequestAborted);
        var actorUsername = actorAccount?.Username
            ?? actor.FindFirst(ClaimTypes.Name)?.Value
            ?? actor.FindFirst("name")?.Value
            ?? "platform-admin";

        // One elevated context at a time: support access and impersonation are mutually exclusive.
        var now = DateTimeOffset.UtcNow;
        var hasSupportAccess = await db.TenantSupportAccessSessions
            .IgnoreQueryFilters()
            .AnyAsync(s => (s.PlatformAdminUserAccountId == actorUserId.Value
                            || (actorAccountId.HasValue && s.PlatformAdminUserAccountId == actorAccountId.Value))
                           && s.Status == SupportAccessStatus.Active
                           && s.ExpiresAt > now,
                context.RequestAborted);
        if (hasSupportAccess)
        {
            return Fail("End the active support access session before starting impersonation.");
        }

        var hasActiveImpersonation = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s => s.Status == UserImpersonationStatus.Active
                           && s.ExpiresAt > now
                           && (s.ActorUserId == actorUserId.Value
                               || (actorAccountId.HasValue && s.ActorUserAccountId == actorAccountId.Value)),
                context.RequestAborted);
        if (hasActiveImpersonation)
        {
            return Fail("An impersonation session is already active.");
        }

        using var crossTenant = TenantFilterScope.BeginCrossTenantScope();
        var target = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetUserId, context.RequestAborted);
        if (target is null)
        {
            return Fail("The selected user does not exist.");
        }

        if (target.Status != UserStatus.Active)
        {
            return Fail("The selected user is deactivated.");
        }

        if (target.Id == actorUserId.Value)
        {
            return Fail("You cannot impersonate yourself.");
        }

        var targetAccount = await userAccounts.FindForUserAsync(target, context.RequestAborted);
        if (targetAccount is null)
        {
            return Fail("The selected user has no account.");
        }

        if (targetAccount.Id == actorAccountId)
        {
            return Fail("You cannot impersonate your own account.");
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == target.TenantId, context.RequestAborted);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return Fail("The selected user's tenant is not active.");
        }

        var resolvedMinutes = Math.Clamp(expiryMinutes ?? DefaultExpiryMinutes, 1, 60);
        var expiresAt = now.AddMinutes(resolvedMinutes);

        var actorAcr = actor.FindFirst(OidcConstants.Claims.Acr)?.Value;
        var actorAmr = actor.Claims
            .Where(c => c.Type == OidcConstants.Claims.Amr)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var actorIdp = actor.FindFirst(OidcConstants.Claims.Idp)?.Value;
        var ipHash = audit.HashValue(context.Connection.RemoteIpAddress?.ToString());
        var userAgent = context.Request.Headers.UserAgent.ToString();

        OpSession targetSession;
        try
        {
            using var sessionScope = TenantFilterScope.BeginCrossTenantScope();
            targetSession = await opSessions.CreateAsync(
                tenant.Id,
                target.Id,
                targetAccount.Id,
                acr: string.IsNullOrWhiteSpace(actorAcr) ? ImpersonationAcr : actorAcr,
                amrJson: actorAmr.Length > 0
                    ? string.Join(' ', actorAmr.Append("impersonation"))
                    : "impersonation",
                upstreamIdp: "impersonation",
                ipHash: ipHash,
                userAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                authTime: now,
                ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not create the target OP session for impersonation");
            return Fail("Could not start impersonation.");
        }

        var session = new UserImpersonationSession
        {
            Id = GuidHelper.NewId(),
            ActorUserAccountId = actorAccountId ?? actorUserId.Value,
            ActorUserId = actorUserId.Value,
            ActorUsername = actorUsername,
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            TenantSlug = tenant.Slug,
            TargetUserId = target.Id,
            TargetUserAccountId = targetAccount.Id,
            TargetUsername = target.Username,
            TargetDisplayName = target.Name ?? targetAccount.Name,
            TargetSecurityStamp = targetAccount.SecurityStamp,
            TargetOpSessionId = targetSession.Id,
            Status = UserImpersonationStatus.Active,
            Reason = reason,
            TicketReference = ticketReference,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            LastSeenAt = now,
            CreatedFromIpHash = ipHash,
            UserAgentHash = audit.HashValue(userAgent),
            ActorAcr = actorAcr,
            ActorAmrJson = actorAmr.Length > 0 ? string.Join(' ', actorAmr) : null,
            ActorIdp = actorIdp
        };

        try
        {
            await store.CreateAsync(session, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist the impersonation session");
            await store.RevokeTargetSessionAsync(targetSession.Id, "Impersonation start failed", context.RequestAborted);
            return Fail("Could not start impersonation.");
        }

        context.Session.SetString(SessionKeyName, session.Id.ToString());
        context.Session.SetString(TenantSessionKeys.PreferredTenantId, tenant.Id.ToString());
        context.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenant.Slug);

        audit.Emit("user_impersonation.started", new
        {
            session_id = session.Id.ToString(),
            actor_account_id = session.ActorUserAccountId.ToString(),
            actor_user_id = session.ActorUserId.ToString(),
            actor_username = session.ActorUsername,
            target_account_id = session.TargetUserAccountId.ToString(),
            target_user_id = session.TargetUserId.ToString(),
            target_username = session.TargetUsername,
            tenant_id = session.TenantId.ToString(),
            reason = session.Reason,
            ticket_reference = session.TicketReference,
            expires_at = session.ExpiresAt.ToUniversalTime().ToString("O")
        });
        metrics.Starts.Add(1, new KeyValuePair<string, object?>("tenant_id", tenant.Slug));

        logger.LogWarning(
            "Platform admin {Actor} started impersonating {Target} in tenant {Tenant} (session {SessionId}, expires {ExpiresAt})",
            actorUsername, target.Username, tenant.Slug, session.Id, expiresAt);

        return new UserImpersonationStartResult
        {
            Success = true,
            SessionId = session.Id,
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            TenantName = tenant.Name,
            TargetUsername = target.Username,
            TargetDisplayName = session.TargetDisplayName,
            ExpiresAt = expiresAt
        };
    }

    public async Task<bool> StopAsync(HttpContext context)
    {
        var raw = context.Session.GetString(SessionKeyName);
        if (!Guid.TryParse(raw, out var sessionId))
        {
            context.Session.Remove(SessionKeyName);
            return false;
        }

        var session = await store.GetByIdAsync(sessionId, context.RequestAborted);
        if (session is null)
        {
            context.Session.Remove(SessionKeyName);
            ClearPreferredTenant(context);
            return false;
        }

        var actorAccountId = ParseGuid(context.User.FindFirst(UserClaimTypes.UserAccountId)?.Value);
        var actorUserId = ParseGuid(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (session.ActorUserAccountId != actorAccountId
            && session.ActorUserId != actorUserId)
        {
            logger.LogWarning("Refusing to stop impersonation session {SessionId}: actor mismatch", sessionId);
            return false;
        }

        var ended = await store.EndAsync(
            sessionId,
            UserImpersonationStatus.Ended,
            DateTimeOffset.UtcNow,
            null,
            null,
            context.RequestAborted);

        context.Session.Remove(SessionKeyName);
        ClearPreferredTenant(context);

        if (!ended)
        {
            return false;
        }

        audit.Emit("user_impersonation.ended", new
        {
            session_id = session.Id.ToString(),
            actor_account_id = session.ActorUserAccountId.ToString(),
            target_account_id = session.TargetUserAccountId.ToString(),
            target_username = session.TargetUsername,
            tenant_id = session.TenantId.ToString(),
            duration_seconds = (int)(DateTimeOffset.UtcNow - session.CreatedAt).TotalSeconds
        });
        metrics.Stops.Add(1, new KeyValuePair<string, object?>("tenant_id", session.TenantSlug));
        metrics.SessionDuration.Record(
            (DateTimeOffset.UtcNow - session.CreatedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("end_reason", "ended"));

        logger.LogInformation("Impersonation session {SessionId} ended by the actor", sessionId);
        return true;
    }

    public async Task<bool> RevokeAsync(ClaimsPrincipal revoker, Guid sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            return false;
        }

        if (!(await authorizationService.AuthorizeAsync(revoker, "platform-admin")).Succeeded)
        {
            return false;
        }

        var revokerAccountId = ParseGuid(revoker.FindFirst(UserClaimTypes.UserAccountId)?.Value)
            ?? ParseGuid(revoker.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (revokerAccountId is null)
        {
            return false;
        }

        var session = await store.GetByIdAsync(sessionId);
        if (session is null || session.Status != UserImpersonationStatus.Active)
        {
            return false;
        }

        var revoked = await store.EndAsync(
            sessionId,
            UserImpersonationStatus.Revoked,
            DateTimeOffset.UtcNow,
            revokerAccountId,
            reason.Trim());
        if (!revoked)
        {
            return false;
        }

        audit.Emit("user_impersonation.revoked", new
        {
            session_id = sessionId.ToString(),
            actor_account_id = session.ActorUserAccountId.ToString(),
            target_account_id = session.TargetUserAccountId.ToString(),
            tenant_id = session.TenantId.ToString(),
            revoker_account_id = revokerAccountId.ToString(),
            reason = reason.Trim()
        });
        metrics.Revocations.Add(1, new KeyValuePair<string, object?>("tenant_id", session.TenantSlug));
        return true;
    }

    public Guid? GetSessionId(HttpContext context)
    {
        var raw = context.Session.IsAvailable ? context.Session.GetString(SessionKeyName) : null;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public async Task<UserImpersonationInfo?> GetInfoAsync(HttpContext context)
    {
        var sessionId = GetSessionId(context);
        if (sessionId is null)
        {
            return null;
        }

        var session = await store.GetByIdAsync(sessionId.Value, context.RequestAborted);
        if (session is null || session.Status != UserImpersonationStatus.Active || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        // The banner also renders on tenant pages, where the overlay principal is the target. Prefer the
        // actor claims carried by the overlay, then fall back to the administrator cookie on control paths.
        var actorAccountId = ParseGuid(context.User.FindFirst(ActorAccountClaim)?.Value)
            ?? ParseGuid(context.User.FindFirst(UserClaimTypes.UserAccountId)?.Value);
        var actorUserId = ParseGuid(context.User.FindFirst(ActorUserClaim)?.Value)
            ?? ParseGuid(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (session.ActorUserAccountId != actorAccountId && session.ActorUserId != actorUserId)
        {
            return null;
        }

        return new UserImpersonationInfo
        {
            SessionId = session.Id,
            TenantId = session.TenantId,
            TenantName = session.TenantName,
            TenantSlug = session.TenantSlug,
            ActorUsername = session.ActorUsername,
            ActorUserAccountId = session.ActorUserAccountId,
            TargetUserId = session.TargetUserId,
            TargetUserAccountId = session.TargetUserAccountId,
            TargetUsername = session.TargetUsername,
            TargetDisplayName = session.TargetDisplayName,
            Reason = session.Reason,
            TicketReference = session.TicketReference,
            StartedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            Status = session.Status
        };
    }

    public async Task<UserImpersonationOverlay?> ResolveOverlayAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        CancellationToken ct = default)
    {
        if (!authOptions.Value.EnableUserImpersonation)
        {
            return null;
        }

        var sessionId = GetSessionId(context);
        if (sessionId is null)
        {
            return null;
        }

        var session = await store.GetByIdAsync(sessionId.Value, ct);
        if (session is null)
        {
            context.Session.Remove(SessionKeyName);
            return null;
        }

        var actorAccountId = ParseGuid(actor.FindFirst(UserClaimTypes.UserAccountId)?.Value);
        var actorUserId = ParseGuid(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (session.ActorUserAccountId != actorAccountId && session.ActorUserId != actorUserId)
        {
            await FailAsync(context, session, "actor_mismatch", ct);
            return null;
        }

        if (session.Status != UserImpersonationStatus.Active || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await FailAsync(context, session, "expired", ct);
            return null;
        }

        if (!(await authorizationService.AuthorizeAsync(actor, "platform-admin")).Succeeded)
        {
            await FailAsync(context, session, "platform_admin_role_missing", ct);
            return null;
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == session.TenantId, ct);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            await FailAsync(context, session, "tenant_inactive", ct);
            return null;
        }

        var target = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.TargetUserId && u.TenantId == session.TenantId, ct);
        if (target is null || target.Status != UserStatus.Active)
        {
            await FailAsync(context, session, "target_inactive", ct);
            return null;
        }

        var account = await userAccounts.FindForUserAsync(target, ct);
        if (account is null || account.Id != session.TargetUserAccountId)
        {
            await FailAsync(context, session, "target_session_invalid", ct);
            return null;
        }

        if (!string.IsNullOrEmpty(session.TargetSecurityStamp)
            && !string.Equals(account.SecurityStamp, session.TargetSecurityStamp, StringComparison.Ordinal))
        {
            await FailAsync(context, session, "security_stamp_changed", ct);
            return null;
        }

        var targetSessionStatus = await opSessions.CheckGlobalAsync(session.TargetOpSessionId, ct);
        if (targetSessionStatus != OpSessionStatus.Valid)
        {
            await FailAsync(context, session, "target_session_invalid", ct);
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, target.Id.ToString()),
            new(ClaimTypes.Name, target.Username),
            new(UserClaimTypes.UserAccountId, account.Id.ToString()),
            new(OidcConstants.Claims.AuthTime, session.CreatedAt.ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Acr, session.ActorAcr ?? ImpersonationAcr),
            new(OidcConstants.Claims.Idp, "impersonation"),
            new(OidcConstants.Claims.Sid, session.TargetOpSessionId.ToString("N")),
            new(OidcConstants.Claims.TenantId, session.TenantId.ToString()),
            new(SessionClaim, session.Id.ToString()),
            new(ActorAccountClaim, session.ActorUserAccountId.ToString()),
            new(ActorUserClaim, session.ActorUserId.ToString())
        };

        if (!string.IsNullOrWhiteSpace(target.Email))
        {
            claims.Add(new(ClaimTypes.Email, target.Email));
        }

        if (!string.IsNullOrWhiteSpace(account.Name))
        {
            claims.Add(new("name", account.Name));
        }
        else if (!string.IsNullOrWhiteSpace(target.Name))
        {
            claims.Add(new("name", target.Name));
        }

        if (!string.IsNullOrWhiteSpace(account.SecurityStamp))
        {
            claims.Add(new("mrwho:sec_stamp", account.SecurityStamp));
        }

        var amr = (session.ActorAmrJson ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append("impersonation")
            .Distinct(StringComparer.Ordinal);
        foreach (var value in amr)
        {
            claims.Add(new(OidcConstants.Claims.Amr, value));
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "impersonation");
        var principal = new ClaimsPrincipal(identity);

        if (session.LastSeenAt is null || DateTimeOffset.UtcNow - session.LastSeenAt.Value >= TimeSpan.FromMinutes(1))
        {
            // Best effort: the request must not fail because the heartbeat could not be written.
            try
            {
                session.LastSeenAt = DateTimeOffset.UtcNow;
                session.ConcurrencyToken = GuidHelper.NewId();
                await store.UpdateAsync(session, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not update the impersonation heartbeat");
            }
        }

        return new UserImpersonationOverlay
        {
            Session = session,
            TargetUser = target,
            TargetAccount = account,
            Principal = principal
        };
    }

    private async Task FailAsync(HttpContext context, UserImpersonationSession session, string reason, CancellationToken ct)
    {
        logger.LogWarning(
            "Invalidating impersonation session {SessionId} for actor {Actor}: {Reason}",
            session.Id, session.ActorUsername, reason);

        metrics.ValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
        audit.Emit("user_impersonation.validation_failed", new
        {
            session_id = session.Id.ToString(),
            actor_account_id = session.ActorUserAccountId.ToString(),
            target_account_id = session.TargetUserAccountId.ToString(),
            tenant_id = session.TenantId.ToString(),
            reason,
            path = context.Request.Path.Value
        });

        context.Session.Remove(SessionKeyName);
        ClearPreferredTenant(context);

        try
        {
            var status = reason == "expired" ? UserImpersonationStatus.Expired : UserImpersonationStatus.Revoked;
            await store.EndAsync(session.Id, status, DateTimeOffset.UtcNow, null, reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not end the invalid impersonation session {SessionId}", session.Id);
        }
    }

    private async Task<Guid?> ResolveActorAccountIdAsync(Guid actorUserId, CancellationToken ct)
    {
        var linked = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == actorUserId)
            .Select(u => u.UserAccountId)
            .FirstOrDefaultAsync(ct);
        return linked;
    }

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var id) ? id : null;

    private static void ClearPreferredTenant(HttpContext context)
    {
        context.Session.Remove(TenantSessionKeys.PreferredTenantId);
        context.Session.Remove(TenantSessionKeys.PreferredTenantSlug);
    }
}
