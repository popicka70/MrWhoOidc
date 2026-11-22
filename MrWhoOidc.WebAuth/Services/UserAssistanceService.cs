using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Assistance;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Implements the consent-based assistance lifecycle. The requester creates the consent record, the invited
/// helper accepts it, and the target OP session is created only at acceptance. The requester is the subject
/// and the helper is the actor.
/// </summary>
public sealed class UserAssistanceService(
    AuthDbContext db,
    IUserAssistanceStore store,
    UserAssistanceRoleResolver roleResolver,
    ITenantSettingsService settingsService,
    IOpSessionService opSessions,
    IUserAccountService userAccounts,
    IEmailSender emailSender,
    IOptions<AuthOptions> authOptions,
    IOptions<OidcOptions> oidcOptions,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantAccessor tenantAccessor,
    IAuditSink audit,
    IUserAssistanceMetrics metrics,
    ILogger<UserAssistanceService> logger) : IUserAssistanceService
{
    public const string SessionKeyName = "UserAssistanceSessionId";
    private const string AssistanceAcr = "urn:mrwho:acr:assistance";
    private const string ActorAccountClaim = UserAssistanceClaimTypes.HelperUserAccountId;
    private const string ActorUserClaim = UserAssistanceClaimTypes.HelperUserId;

    public string SessionIdKey => SessionKeyName;

    public async Task<UserAssistanceActionResult> CreateRequestAsync(
        HttpContext context,
        ClaimsPrincipal requester,
        Guid helperUserId,
        UserAssistanceMode mode,
        string reason,
        int? durationMinutes,
        string? ticketReference,
        CancellationToken ct = default)
    {
        static UserAssistanceActionResult Fail(string error) => UserAssistanceActionResult.Fail(error);

        if (!authOptions.Value.EnableUserAssistance)
        {
            return Fail("User assistance is disabled.");
        }

        if (!TryGetUserIds(requester, out var requesterUserId, out var requesterAccountId))
        {
            return Fail("The requester identity could not be resolved.");
        }

        reason = (reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            return Fail("A reason is required.");
        }

        if (reason.Length > 500)
        {
            return Fail("The reason must be at most 500 characters.");
        }

        ticketReference = NormalizeOptional(ticketReference);
        if (ticketReference is { Length: > 100 })
        {
            return Fail("The ticket reference must be at most 100 characters.");
        }

        var requesterUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == requesterUserId, ct)
            .ConfigureAwait(false);
        if (requesterUser is null || requesterUser.Status != UserStatus.Active)
        {
            return Fail("The requester account is not active.");
        }

        var requesterAccount = await userAccounts.FindForUserAsync(requesterUser, ct).ConfigureAwait(false);
        if (requesterAccount is null || requesterAccount.Id != requesterAccountId)
        {
            return Fail("The requester account could not be resolved.");
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (currentTenantId is { } resolvedTenantId && resolvedTenantId != requesterUser.TenantId)
        {
            return Fail("The requester is not active in the current tenant.");
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == requesterUser.TenantId, ct)
            .ConfigureAwait(false);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return Fail("The tenant is not active.");
        }

        var settings = AssistanceSettingsResolver.Resolve((await settingsService
            .GetTenantSettingsAsync(tenant.Id)
            .ConfigureAwait(false))?.Assistance);
        if (!settings.Enabled)
        {
            return Fail("User assistance is disabled for this tenant.");
        }

        var requesterRoles = await roleResolver.GetActiveRolesAsync(requesterUser.Id, tenant.Id, ct)
            .ConfigureAwait(false);
        if (!settings.IsRequesterAllowed(requesterRoles.Values))
        {
            return Fail("Your roles do not allow requesting assistance.");
        }

        if (helperUserId == Guid.Empty)
        {
            return Fail("Select a helper.");
        }

        if (helperUserId == requesterUser.Id)
        {
            return Fail("You cannot request assistance from yourself.");
        }

        var helperUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == helperUserId && u.TenantId == tenant.Id, ct)
            .ConfigureAwait(false);
        if (helperUser is null || helperUser.Status != UserStatus.Active)
        {
            return Fail("The selected helper is not an active member of this tenant.");
        }

        var helperAccount = await userAccounts.FindForUserAsync(helperUser, ct).ConfigureAwait(false);
        if (helperAccount is null)
        {
            return Fail("The selected helper has no account.");
        }

        var helperRoles = await roleResolver.GetActiveRolesAsync(helperUser.Id, tenant.Id, ct)
            .ConfigureAwait(false);
        var helperCanReadOnly = settings.IsReadOnlyHelper(helperRoles.Values);
        var helperCanReadWrite = settings.IsReadWriteHelper(helperRoles.Values);
        if (mode == UserAssistanceMode.ReadWrite && !helperCanReadWrite)
        {
            return Fail("The selected helper does not hold a read-write assistance role.");
        }

        if (mode == UserAssistanceMode.ReadOnly && !helperCanReadOnly)
        {
            return Fail("The selected helper does not hold an assistance role.");
        }

        var now = DateTimeOffset.UtcNow;
        var effectiveDuration = Math.Clamp(
            durationMinutes ?? settings.DefaultDurationMinutes,
            1,
            settings.MaxDurationMinutes);

        // One active assistance context per person, and no duplicate pending request from the same requester.
        var existingPending = await store.GetPendingForRequesterAsync(requesterUser.Id, ct).ConfigureAwait(false);
        if (existingPending is not null)
        {
            return Fail("You already have a pending assistance request.");
        }

        var conflict = await FindActiveAssistanceConflictAsync(requesterUser.Id, requesterAccountId, ct)
            .ConfigureAwait(false);
        if (conflict is not null)
        {
            return Fail("End the active assistance session before requesting help.");
        }

        if (await HasExternalElevatedContextAsync(requesterUser.Id, requesterAccountId, now, ct).ConfigureAwait(false))
        {
            return Fail("End the active impersonation or support access session before requesting help.");
        }

        var conflictForHelper = await FindActiveAssistanceConflictAsync(helperUser.Id, helperAccount.Id, ct)
            .ConfigureAwait(false);
        if (conflictForHelper is not null)
        {
            return Fail("The selected helper already has an active assistance session.");
        }

        if (await HasExternalElevatedContextAsync(helperUser.Id, helperAccount.Id, now, ct).ConfigureAwait(false))
        {
            return Fail("The selected helper already has an elevated access session.");
        }

        var rawToken = CryptoHelper.GenerateSecureRandomString(32);
        var expiresAt = now.AddMinutes(settings.RequestLifetimeMinutes);

        var session = new UserAssistanceSession
        {
            Id = GuidHelper.NewId(),
            TenantId = tenant.Id,
            RequesterUserId = requesterUser.Id,
            RequesterUserAccountId = requesterAccountId,
            RequesterUsername = requesterUser.Username,
            RequesterDisplayName = requesterUser.Name ?? requesterAccount.Name,
            HelperUserId = helperUser.Id,
            HelperUserAccountId = helperAccount.Id,
            HelperUsername = helperUser.Username,
            HelperEmail = helperUser.Email ?? helperAccount.Email,
            Mode = mode,
            RightsMode = settings.RightsMode,
            Status = UserAssistanceStatus.Pending,
            Reason = reason,
            TicketReference = ticketReference,
            RequestedDurationMinutes = effectiveDuration,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            InvitationTokenHash = CryptoHelper.ComputeSha256Base64(rawToken),
            CreatedFromIpHash = audit.HashValue(context.Connection.RemoteIpAddress?.ToString()),
            UserAgentHash = audit.HashValue(context.Request.Headers.UserAgent.ToString())
        };

        try
        {
            await store.CreateAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist the user assistance request");
            return Fail("Could not create the assistance request.");
        }

        var acceptLink = BuildAcceptLink(context, tenant, session.Id, rawToken);
        await SendNotificationAsync(
            BuildRequestEmail(session, tenant, acceptLink),
            ct).ConfigureAwait(false);

        audit.Emit("user_assistance.requested", new
        {
            request_id = session.Id.ToString(),
            requester_id = session.RequesterUserId.ToString(),
            requester_account_id = session.RequesterUserAccountId.ToString(),
            helper_id = session.HelperUserId.ToString(),
            helper_account_id = session.HelperUserAccountId.ToString(),
            tenant_id = session.TenantId.ToString(),
            mode = session.Mode.ToString(),
            rights_mode = session.RightsMode.ToString(),
            reason = session.Reason,
            expires_at = session.ExpiresAt.ToUniversalTime().ToString("O")
        });
        metrics.Requests.Add(1, new KeyValuePair<string, object?>("tenant_id", tenant.Slug));

        logger.LogInformation(
            "Assistance request {RequestId} created by {Requester} for helper {Helper} in tenant {Tenant}",
            session.Id, requesterUser.Username, helperUser.Username, tenant.Slug);

        return new UserAssistanceActionResult
        {
            Success = true,
            RequestId = session.Id,
            Status = session.Status
        };
    }

    public async Task<UserAssistanceActionResult> AcceptAsync(
        HttpContext context,
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default)
    {
        static UserAssistanceActionResult Fail(string error) => UserAssistanceActionResult.Fail(error);

        if (!authOptions.Value.EnableUserAssistance)
        {
            return Fail("User assistance is disabled.");
        }

        if (!TryGetUserIds(helper, out var helperUserId, out var helperAccountId))
        {
            return Fail("The helper identity could not be resolved.");
        }

        var request = await ResolvePendingRequestAsync(requestId, token, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Fail("This assistance request is no longer available.");
        }

        if (request.Status != UserAssistanceStatus.Pending || request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Fail("This assistance request has expired.");
        }

        if (request.HelperUserId != helperUserId && request.HelperUserAccountId != helperAccountId)
        {
            return Fail("This assistance request was sent to a different user.");
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, ct)
            .ConfigureAwait(false);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return Fail("The tenant is not active.");
        }

        var helperUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == helperUserId && u.TenantId == tenant.Id, ct)
            .ConfigureAwait(false);
        if (helperUser is null || helperUser.Status != UserStatus.Active)
        {
            return Fail("The helper account is not active in this tenant.");
        }

        var requesterUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.RequesterUserId && u.TenantId == tenant.Id, ct)
            .ConfigureAwait(false);
        if (requesterUser is null || requesterUser.Status != UserStatus.Active)
        {
            return Fail("The requester account is not active.");
        }

        var requesterAccount = await userAccounts.FindForUserAsync(requesterUser, ct).ConfigureAwait(false);
        if (requesterAccount is null || requesterAccount.Id != request.RequesterUserAccountId)
        {
            return Fail("The requester account could not be resolved.");
        }

        var settings = AssistanceSettingsResolver.Resolve((await settingsService
            .GetTenantSettingsAsync(tenant.Id)
            .ConfigureAwait(false))?.Assistance);
        if (!settings.Enabled)
        {
            return Fail("User assistance is disabled for this tenant.");
        }

        var helperRoles = await roleResolver.GetActiveRolesAsync(helperUser.Id, tenant.Id, ct)
            .ConfigureAwait(false);
        if (request.Mode == UserAssistanceMode.ReadWrite
            && !settings.IsReadWriteHelper(helperRoles.Values))
        {
            return Fail("The helper no longer holds a read-write assistance role.");
        }

        if (request.Mode == UserAssistanceMode.ReadOnly
            && !settings.IsReadOnlyHelper(helperRoles.Values))
        {
            return Fail("The helper no longer holds an assistance role.");
        }

        var now = DateTimeOffset.UtcNow;
        var conflict = await FindActiveAssistanceConflictAsync(helperUser.Id, helperAccountId, ct)
            .ConfigureAwait(false);
        if (conflict is not null)
        {
            return Fail("End your active assistance session before accepting another request.");
        }

        if (await HasExternalElevatedContextAsync(helperUser.Id, helperAccountId, now, ct).ConfigureAwait(false))
        {
            return Fail("End your active impersonation or support access session before accepting assistance.");
        }

        if (await HasExternalElevatedContextAsync(requesterUser.Id, requesterAccount.Id, now, ct).ConfigureAwait(false))
        {
            return Fail("The requester has an active impersonation or support access session.");
        }

        var effectiveRoleIds = await roleResolver.GetEffectiveRoleIdsAsync(request, ct).ConfigureAwait(false);

        var actorAcr = helper.FindFirst(OidcConstants.Claims.Acr)?.Value;
        var actorAmr = helper.Claims
            .Where(c => c.Type == OidcConstants.Claims.Amr)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var actorIdp = helper.FindFirst(OidcConstants.Claims.Idp)?.Value;
        var ipHash = audit.HashValue(context.Connection.RemoteIpAddress?.ToString());
        var userAgent = context.Request.Headers.UserAgent.ToString();

        OpSession targetSession;
        try
        {
            using var sessionScope = TenantFilterScope.BeginCrossTenantScope();
            targetSession = await opSessions.CreateAsync(
                tenant.Id,
                requesterUser.Id,
                requesterAccount.Id,
                acr: string.IsNullOrWhiteSpace(actorAcr) ? AssistanceAcr : actorAcr,
                amrJson: actorAmr.Length > 0
                    ? string.Join(' ', actorAmr.Append("assistance"))
                    : "assistance",
                upstreamIdp: "assistance",
                ipHash: ipHash,
                userAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                authTime: now,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not create the target OP session for assistance");
            return Fail("Could not accept the assistance request.");
        }

        request.Status = UserAssistanceStatus.Active;
        request.AcceptedAt = now;
        request.SessionExpiresAt = now.AddMinutes(Math.Max(1, request.RequestedDurationMinutes));
        request.TargetOpSessionId = targetSession.Id;
        request.TargetSecurityStamp = requesterAccount.SecurityStamp;
        request.ActorAcr = actorAcr;
        request.ActorAmrJson = actorAmr.Length > 0 ? string.Join(' ', actorAmr) : null;
        request.ActorIdp = actorIdp;
        request.InvitationTokenHash = null;
        request.LastSeenAt = now;
        request.EffectiveRoleIdsJson = UserAssistanceRoleResolver.SerializeRoleIds(effectiveRoleIds);

        try
        {
            await store.UpdateAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not activate assistance request {RequestId}", request.Id);
            try
            {
                await store.RevokeTargetSessionAsync(targetSession.Id, "Assistance acceptance failed", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception revokeEx)
            {
                logger.LogDebug(revokeEx, "Could not revoke failed assistance target session");
            }

            return Fail("Could not accept the assistance request.");
        }

        context.Session.SetString(SessionKeyName, request.Id.ToString());
        context.Session.SetString(TenantSessionKeys.PreferredTenantId, tenant.Id.ToString());
        context.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenant.Slug);

        await SendNotificationAsync(
            BuildAcceptedEmail(request, requesterUser, requesterAccount, tenant),
            ct).ConfigureAwait(false);

        audit.Emit("user_assistance.accepted", new
        {
            request_id = request.Id.ToString(),
            session_id = request.Id.ToString(),
            requester_id = request.RequesterUserId.ToString(),
            requester_account_id = request.RequesterUserAccountId.ToString(),
            helper_id = request.HelperUserId.ToString(),
            helper_account_id = request.HelperUserAccountId.ToString(),
            tenant_id = request.TenantId.ToString(),
            mode = request.Mode.ToString(),
            rights_mode = request.RightsMode.ToString(),
            ip_hash = ipHash,
            user_agent_hash = audit.HashValue(userAgent),
            expires_at = request.SessionExpiresAt?.ToUniversalTime().ToString("O")
        });
        metrics.Accepts.Add(1, new KeyValuePair<string, object?>("tenant_id", tenant.Slug));

        logger.LogWarning(
            "Helper {Helper} accepted assistance request {RequestId} for requester {Requester} in tenant {Tenant}",
            helperUser.Username, request.Id, requesterUser.Username, tenant.Slug);

        return new UserAssistanceActionResult
        {
            Success = true,
            RequestId = request.Id,
            SessionId = request.Id,
            Status = request.Status
        };
    }

    public async Task<UserAssistanceActionResult> DeclineAsync(
        HttpContext context,
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default)
    {
        static UserAssistanceActionResult Fail(string error) => UserAssistanceActionResult.Fail(error);

        if (!authOptions.Value.EnableUserAssistance)
        {
            return Fail("User assistance is disabled.");
        }

        if (!TryGetUserIds(helper, out var helperUserId, out var helperAccountId))
        {
            return Fail("The helper identity could not be resolved.");
        }

        var request = await ResolvePendingRequestAsync(requestId, token, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Fail("This assistance request is no longer available.");
        }

        if (request.HelperUserId != helperUserId && request.HelperUserAccountId != helperAccountId)
        {
            return Fail("This assistance request was sent to a different user.");
        }

        var ended = await store.EndAsync(
            request.Id,
            UserAssistanceStatus.Declined,
            DateTimeOffset.UtcNow,
            helperAccountId,
            "Declined by helper",
            ct).ConfigureAwait(false);
        if (!ended)
        {
            return Fail("This assistance request is no longer available.");
        }

        var requesterUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.RequesterUserId, ct)
            .ConfigureAwait(false);
        if (requesterUser is not null)
        {
            await SendNotificationAsync(
                BuildDeclinedEmail(request, requesterUser),
                ct).ConfigureAwait(false);
        }

        audit.Emit("user_assistance.declined", new
        {
            request_id = request.Id.ToString(),
            requester_id = request.RequesterUserId.ToString(),
            helper_id = request.HelperUserId.ToString(),
            tenant_id = request.TenantId.ToString()
        });
        metrics.Declines.Add(1);

        return new UserAssistanceActionResult
        {
            Success = true,
            RequestId = request.Id,
            Status = UserAssistanceStatus.Declined
        };
    }

    public async Task<UserAssistanceActionResult> CancelAsync(
        HttpContext context,
        ClaimsPrincipal requester,
        Guid requestId,
        CancellationToken ct = default)
    {
        static UserAssistanceActionResult Fail(string error) => UserAssistanceActionResult.Fail(error);

        if (!authOptions.Value.EnableUserAssistance)
        {
            return Fail("User assistance is disabled.");
        }

        if (!TryGetUserIds(requester, out var requesterUserId, out var requesterAccountId))
        {
            return Fail("The requester identity could not be resolved.");
        }

        var request = await store.GetByIdAsync(requestId, ct).ConfigureAwait(false);
        if (request is null
            || request.Status != UserAssistanceStatus.Pending
            || (request.RequesterUserId != requesterUserId
                && request.RequesterUserAccountId != requesterAccountId))
        {
            return Fail("This assistance request is no longer available.");
        }

        var ended = await store.EndAsync(
            request.Id,
            UserAssistanceStatus.Cancelled,
            DateTimeOffset.UtcNow,
            requesterAccountId,
            "Cancelled by requester",
            ct).ConfigureAwait(false);
        if (!ended)
        {
            return Fail("This assistance request is no longer available.");
        }

        var helperUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.HelperUserId, ct)
            .ConfigureAwait(false);
        if (helperUser is not null)
        {
            await SendNotificationAsync(
                BuildCancelledEmail(request, helperUser),
                ct).ConfigureAwait(false);
        }

        audit.Emit("user_assistance.cancelled", new
        {
            request_id = request.Id.ToString(),
            requester_id = request.RequesterUserId.ToString(),
            helper_id = request.HelperUserId.ToString(),
            tenant_id = request.TenantId.ToString()
        });
        metrics.Cancels.Add(1);

        return new UserAssistanceActionResult
        {
            Success = true,
            RequestId = request.Id,
            Status = UserAssistanceStatus.Cancelled
        };
    }

    public async Task<UserAssistanceActionResult> EndAsync(
        HttpContext context,
        ClaimsPrincipal user,
        Guid sessionId,
        string? reason = null,
        CancellationToken ct = default)
    {
        static UserAssistanceActionResult Fail(string error) => UserAssistanceActionResult.Fail(error);

        if (!authOptions.Value.EnableUserAssistance)
        {
            return Fail("User assistance is disabled.");
        }

        if (!TryGetUserIds(user, out var userId, out var accountId))
        {
            return Fail("The identity could not be resolved.");
        }

        var session = await store.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != UserAssistanceStatus.Active)
        {
            return Fail("This assistance session is no longer active.");
        }

        if (session.SessionExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            await store.EndAsync(
                session.Id,
                UserAssistanceStatus.Expired,
                DateTimeOffset.UtcNow,
                null,
                "Expired",
                ct).ConfigureAwait(false);
            metrics.Expirations.Add(1);
            return Fail("This assistance session has expired.");
        }

        var isRequester = session.RequesterUserId == userId || session.RequesterUserAccountId == accountId;
        var isHelper = session.HelperUserId == userId || session.HelperUserAccountId == accountId;
        var isManager = false;

        if (!isRequester && !isHelper)
        {
            var settings = AssistanceSettingsResolver.Resolve((await settingsService
                .GetTenantSettingsAsync(session.TenantId)
                .ConfigureAwait(false))?.Assistance);
            var roles = await roleResolver.GetActiveRolesAsync(userId, session.TenantId, ct)
                .ConfigureAwait(false);
            isManager = settings.IsManager(roles.Values);
        }

        if (!isRequester && !isHelper && !isManager)
        {
            return Fail("You are not allowed to end this assistance session.");
        }

        var status = isManager && !isRequester && !isHelper
            ? UserAssistanceStatus.Revoked
            : UserAssistanceStatus.Ended;
        var cleanReason = NormalizeOptional(reason);
        if (cleanReason is { Length: > 500 })
        {
            cleanReason = cleanReason[..500];
        }

        var endedBy = accountId;
        var ended = await store.EndAsync(
            session.Id,
            status,
            DateTimeOffset.UtcNow,
            endedBy,
            cleanReason ?? (status == UserAssistanceStatus.Revoked ? "Revoked by manager" : "Ended"),
            ct).ConfigureAwait(false);
        if (!ended)
        {
            return Fail("This assistance session is no longer active.");
        }

        if (string.Equals(context.Session.GetString(SessionKeyName), session.Id.ToString(), StringComparison.Ordinal))
        {
            context.Session.Remove(SessionKeyName);
            context.Session.Remove(TenantSessionKeys.PreferredTenantId);
            context.Session.Remove(TenantSessionKeys.PreferredTenantSlug);
        }

        var requesterUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.RequesterUserId, ct)
            .ConfigureAwait(false);
        var helperUser = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.HelperUserId, ct)
            .ConfigureAwait(false);

        if (!isRequester || isManager)
        {
            if (requesterUser is not null)
            {
                await SendNotificationAsync(
                    BuildEndedEmail(session, requesterUser, "requester", status),
                    ct).ConfigureAwait(false);
            }
        }

        if (!isHelper || isManager)
        {
            if (helperUser is not null)
            {
                await SendNotificationAsync(
                    BuildEndedEmail(session, helperUser, "helper", status),
                    ct).ConfigureAwait(false);
            }
        }

        audit.Emit(status == UserAssistanceStatus.Revoked ? "user_assistance.revoked" : "user_assistance.ended", new
        {
            session_id = session.Id.ToString(),
            requester_id = session.RequesterUserId.ToString(),
            helper_id = session.HelperUserId.ToString(),
            tenant_id = session.TenantId.ToString(),
            ender_id = accountId.ToString(),
            ender_role = isManager && !isRequester && !isHelper ? "manager" : (isHelper ? "helper" : "requester"),
            reason = cleanReason,
            duration_seconds = (int)(DateTimeOffset.UtcNow - (session.AcceptedAt ?? session.CreatedAt)).TotalSeconds
        });
        if (status == UserAssistanceStatus.Revoked)
        {
            metrics.Revocations.Add(1);
        }
        else
        {
            metrics.Ends.Add(1);
            metrics.SessionDuration.Record(
                (DateTimeOffset.UtcNow - (session.AcceptedAt ?? session.CreatedAt)).TotalMilliseconds,
                new KeyValuePair<string, object?>("end_reason", "ended"));
        }

        logger.LogInformation(
            "Assistance session {SessionId} ended with status {Status} by account {AccountId}",
            session.Id, status, accountId);

        return new UserAssistanceActionResult
        {
            Success = true,
            SessionId = session.Id,
            Status = status
        };
    }

    public async Task<UserAssistanceActionResult> StopAsync(HttpContext context, CancellationToken ct = default)
    {
        var raw = context.Session.GetString(SessionKeyName);
        if (!Guid.TryParse(raw, out var sessionId))
        {
            context.Session.Remove(SessionKeyName);
            return UserAssistanceActionResult.Fail("There is no active assistance session.");
        }

        return await EndAsync(context, context.User, sessionId, "Ended by helper", ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAssistanceInfo?> GetInfoAsync(HttpContext context)
    {
        var raw = context.Session.GetString(SessionKeyName);
        if (!Guid.TryParse(raw, out var sessionId))
        {
            return null;
        }

        var session = await store.GetByIdAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        if (session is null
            || session.Status != UserAssistanceStatus.Active
            || session.SessionExpiresAt is null
            || session.SessionExpiresAt <= DateTimeOffset.UtcNow)
        {
            context.Session.Remove(SessionKeyName);
            return null;
        }

        var helperAccountId = ParseGuid(context.User.FindFirst(ActorAccountClaim)?.Value)
            ?? ParseGuid(context.User.FindFirst(UserClaimTypes.UserAccountId)?.Value);
        var helperUserId = ParseGuid(context.User.FindFirst(ActorUserClaim)?.Value)
            ?? ParseGuid(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        if (session.HelperUserAccountId != helperAccountId && session.HelperUserId != helperUserId)
        {
            return null;
        }

        return ToInfo(session);
    }

    public async Task<UserAssistanceInfo?> GetRequesterInfoAsync(HttpContext context)
    {
        if (context.Items.ContainsKey(Middleware.UserAssistanceMiddleware.OverlayItemKey))
        {
            return null;
        }

        if (!TryGetUserIds(context.User, out var requesterUserId, out _))
        {
            return null;
        }

        var session = await store.GetActiveForRequesterAsync(requesterUserId, context.RequestAborted)
            .ConfigureAwait(false);
        return session is null ? null : ToInfo(session);
    }

    public async Task<UserAssistancePendingInfo?> GetPendingForAcceptAsync(
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default)
    {
        if (!authOptions.Value.EnableUserAssistance || !TryGetUserIds(helper, out var helperUserId, out var helperAccountId))
        {
            return null;
        }

        var request = await ResolvePendingRequestAsync(requestId, token, ct).ConfigureAwait(false);
        if (request is null || request.Status != UserAssistanceStatus.Pending || request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        if (request.HelperUserId != helperUserId && request.HelperUserAccountId != helperAccountId)
        {
            return null;
        }

        return new UserAssistancePendingInfo
        {
            Session = request,
            Info = ToInfo(request)
        };
    }

    public async Task<ResolvedAssistanceSettings?> GetSettingsAsync(Guid tenantId)
    {
        var tenantSettings = await settingsService.GetTenantSettingsAsync(tenantId).ConfigureAwait(false);
        return tenantSettings is null
            ? null
            : AssistanceSettingsResolver.Resolve(tenantSettings.Assistance);
    }

    public async Task<IReadOnlyList<UserAssistanceHelperCandidate>> ListEligibleHelpersAsync(
        Guid tenantId,
        Guid requesterUserId,
        CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(tenantId).ConfigureAwait(false);
        if (settings is null || !settings.Enabled)
        {
            return Array.Empty<UserAssistanceHelperCandidate>();
        }

        var roles = await db.Roles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsActive)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var readOnlyRoleIds = roles
            .Where(r => settings.ReadOnlyHelperRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Id)
            .ToHashSet();
        var readWriteRoleIds = roles
            .Where(r => settings.ReadWriteHelperRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Id)
            .ToHashSet();
        if (readOnlyRoleIds.Count == 0 && readWriteRoleIds.Count == 0)
        {
            return Array.Empty<UserAssistanceHelperCandidate>();
        }

        var helperRoleIds = readOnlyRoleIds.Concat(readWriteRoleIds).ToHashSet();

        var realmAssignments = await db.UserRealmRoleAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.IsActive && helperRoleIds.Contains(a.RoleId))
            .Join(db.Realms.IgnoreQueryFilters().AsNoTracking(), a => a.RealmId, rl => rl.Id, (a, rl) => new { a.UserId, a.RoleId, rl.TenantId })
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.UserId, x.RoleId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var clientAssignments = await db.UserClientRoleAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.IsActive && helperRoleIds.Contains(a.RoleId))
            .Join(db.Clients.IgnoreQueryFilters().AsNoTracking(), a => a.ClientId, c => c.Id, (a, c) => new { a.UserId, a.RoleId, c.TenantId })
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.UserId, x.RoleId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var assignmentsByUser = realmAssignments
            .Concat(clientAssignments)
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RoleId).ToHashSet());

        var candidateIds = assignmentsByUser.Keys.Where(id => id != requesterUserId).ToList();
        if (candidateIds.Count == 0)
        {
            return Array.Empty<UserAssistanceHelperCandidate>();
        }

        var users = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => candidateIds.Contains(u.Id) && u.TenantId == tenantId && u.Status == UserStatus.Active)
            .OrderBy(u => u.Name ?? u.Username)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var candidates = new List<UserAssistanceHelperCandidate>(users.Count);
        foreach (var user in users)
        {
            var account = await userAccounts.FindForUserAsync(user, ct).ConfigureAwait(false);
            if (account is null)
            {
                continue;
            }

            var roleIds = assignmentsByUser[user.Id];
            var canReadWrite = roleIds.Overlaps(readWriteRoleIds);
            var canReadOnly = canReadWrite || roleIds.Overlaps(readOnlyRoleIds);
            if (!canReadOnly)
            {
                continue;
            }

            candidates.Add(new UserAssistanceHelperCandidate
            {
                UserId = user.Id,
                UserAccountId = account.Id,
                Username = user.Username,
                DisplayName = user.Name ?? account.Name,
                Email = user.Email ?? account.Email,
                CanReadOnly = canReadOnly,
                CanReadWrite = canReadWrite
            });
        }

        return candidates;
    }

    public async Task<IReadOnlyList<UserAssistanceInfo>> ListForRequesterAsync(
        Guid requesterUserId,
        CancellationToken ct = default)
    {
        var sessions = await store.ListForRequesterAsync(requesterUserId, ct).ConfigureAwait(false);
        return sessions.Select(ToInfo).ToArray();
    }

    public async Task<IReadOnlyList<UserAssistanceInfo>> ListForHelperAsync(
        Guid helperUserId,
        CancellationToken ct = default)
    {
        var sessions = await store.ListForHelperAsync(helperUserId, ct).ConfigureAwait(false);
        return sessions.Select(ToInfo).ToArray();
    }

    public async Task<IReadOnlyList<UserAssistanceInfo>> ListForManagerAsync(
        ClaimsPrincipal manager,
        Guid tenantId,
        CancellationToken ct = default)
    {
        if (!TryGetUserIds(manager, out var managerUserId, out _))
        {
            return Array.Empty<UserAssistanceInfo>();
        }

        var settings = await GetSettingsAsync(tenantId).ConfigureAwait(false);
        if (settings is null)
        {
            return Array.Empty<UserAssistanceInfo>();
        }

        var roles = await roleResolver.GetActiveRolesAsync(managerUserId, tenantId, ct).ConfigureAwait(false);
        if (!settings.IsManager(roles.Values))
        {
            return Array.Empty<UserAssistanceInfo>();
        }

        var sessions = await store.ListForTenantAsync(tenantId, ct).ConfigureAwait(false);
        return sessions.Select(ToInfo).ToArray();
    }

    public async Task<UserAssistanceOverlay?> ResolveOverlayAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        CancellationToken ct = default)
    {
        if (!authOptions.Value.EnableUserAssistance)
        {
            return null;
        }

        var raw = context.Session.GetString(SessionKeyName);
        if (!Guid.TryParse(raw, out var sessionId))
        {
            return null;
        }

        var session = await store.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            context.Session.Remove(SessionKeyName);
            return null;
        }

        if (!TryGetUserIds(actor, out var helperUserId, out var helperAccountId))
        {
            await FailAsync(context, session, "actor_mismatch", ct).ConfigureAwait(false);
            return null;
        }

        if (session.HelperUserAccountId != helperAccountId && session.HelperUserId != helperUserId)
        {
            await FailAsync(context, session, "actor_mismatch", ct).ConfigureAwait(false);
            return null;
        }

        if (session.Status != UserAssistanceStatus.Active
            || session.SessionExpiresAt is null
            || session.SessionExpiresAt <= DateTimeOffset.UtcNow)
        {
            await FailAsync(context, session, "expired", ct).ConfigureAwait(false);
            return null;
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == session.TenantId, ct)
            .ConfigureAwait(false);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            await FailAsync(context, session, "tenant_inactive", ct).ConfigureAwait(false);
            return null;
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (currentTenantId is { } resolvedTenantId && resolvedTenantId != session.TenantId)
        {
            return null;
        }

        var requester = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.RequesterUserId && u.TenantId == session.TenantId, ct)
            .ConfigureAwait(false);
        if (requester is null || requester.Status != UserStatus.Active)
        {
            await FailAsync(context, session, "requester_inactive", ct).ConfigureAwait(false);
            return null;
        }

        var requesterAccount = await userAccounts.FindForUserAsync(requester, ct).ConfigureAwait(false);
        if (requesterAccount is null || requesterAccount.Id != session.RequesterUserAccountId)
        {
            await FailAsync(context, session, "requester_inactive", ct).ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrEmpty(session.TargetSecurityStamp)
            && !string.Equals(requesterAccount.SecurityStamp, session.TargetSecurityStamp, StringComparison.Ordinal))
        {
            await FailAsync(context, session, "security_stamp_changed", ct).ConfigureAwait(false);
            return null;
        }

        var settings = AssistanceSettingsResolver.Resolve((await settingsService
            .GetTenantSettingsAsync(session.TenantId)
            .ConfigureAwait(false))?.Assistance);
        if (!settings.Enabled)
        {
            await FailAsync(context, session, "tenant_disabled", ct).ConfigureAwait(false);
            return null;
        }

        var helperRoles = await roleResolver.GetActiveRolesAsync(helperUserId, session.TenantId, ct)
            .ConfigureAwait(false);
        if (session.Mode == UserAssistanceMode.ReadWrite
            && !settings.IsReadWriteHelper(helperRoles.Values))
        {
            await FailAsync(context, session, "role_missing", ct).ConfigureAwait(false);
            return null;
        }

        if (session.Mode == UserAssistanceMode.ReadOnly
            && !settings.IsReadOnlyHelper(helperRoles.Values))
        {
            await FailAsync(context, session, "role_missing", ct).ConfigureAwait(false);
            return null;
        }

        var targetSessionId = session.TargetOpSessionId;
        if (targetSessionId is null)
        {
            await FailAsync(context, session, "target_session_invalid", ct).ConfigureAwait(false);
            return null;
        }

        var targetSessionStatus = await opSessions.CheckGlobalAsync(targetSessionId.Value, ct).ConfigureAwait(false);
        if (targetSessionStatus != OpSessionStatus.Valid)
        {
            await FailAsync(context, session, "target_session_invalid", ct).ConfigureAwait(false);
            return null;
        }

        var effectiveRoleIds = await roleResolver.GetEffectiveRoleIdsAsync(session, ct).ConfigureAwait(false);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, requester.Id.ToString()),
            new(ClaimTypes.Name, requester.Username),
            new(UserClaimTypes.UserAccountId, requesterAccount.Id.ToString()),
            new(OidcConstants.Claims.AuthTime, (session.AcceptedAt ?? session.CreatedAt).ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Acr, session.ActorAcr ?? AssistanceAcr),
            new(OidcConstants.Claims.Idp, "assistance"),
            new(OidcConstants.Claims.Sid, targetSessionId.Value.ToString("N")),
            new(OidcConstants.Claims.TenantId, session.TenantId.ToString()),
            new(UserAssistanceClaimTypes.SessionId, session.Id.ToString()),
            new(UserAssistanceClaimTypes.Mode,
                session.Mode == UserAssistanceMode.ReadOnly
                    ? UserAssistanceClaimTypes.ReadOnlyMode
                    : UserAssistanceClaimTypes.ReadWriteMode),
            new(UserAssistanceClaimTypes.HelperUserId, session.HelperUserId.ToString()),
            new(UserAssistanceClaimTypes.HelperUserAccountId, session.HelperUserAccountId.ToString()),
            new(UserAssistanceClaimTypes.HelperUsername, session.HelperUsername),
            new(UserAssistanceClaimTypes.EffectiveRoleIds, UserAssistanceRoleResolver.SerializeRoleIds(effectiveRoleIds))
        };

        if (!string.IsNullOrWhiteSpace(requester.Email))
        {
            claims.Add(new(ClaimTypes.Email, requester.Email));
        }

        if (!string.IsNullOrWhiteSpace(requesterAccount.Name))
        {
            claims.Add(new("name", requesterAccount.Name));
        }
        else if (!string.IsNullOrWhiteSpace(requester.Name))
        {
            claims.Add(new("name", requester.Name));
        }

        if (!string.IsNullOrWhiteSpace(requesterAccount.SecurityStamp))
        {
            claims.Add(new("mrwho:sec_stamp", requesterAccount.SecurityStamp));
        }

        var amr = (session.ActorAmrJson ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append("assistance")
            .Distinct(StringComparer.Ordinal);
        foreach (var value in amr)
        {
            claims.Add(new(OidcConstants.Claims.Amr, value));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "assistance"));

        if (session.LastSeenAt is null
            || DateTimeOffset.UtcNow - session.LastSeenAt.Value >= TimeSpan.FromMinutes(1))
        {
            try
            {
                session.LastSeenAt = DateTimeOffset.UtcNow;
                await store.UpdateAsync(session, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not update the assistance heartbeat");
            }
        }

        return new UserAssistanceOverlay
        {
            Session = session,
            RequesterUser = requester,
            RequesterAccount = requesterAccount,
            Principal = principal,
            EffectiveRoleIds = effectiveRoleIds
        };
    }

    private async Task<UserAssistanceSession?> ResolvePendingRequestAsync(
        Guid requestId,
        string? token,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            var hash = CryptoHelper.ComputeSha256Base64(token.Trim());
            var byToken = await store.GetPendingByInvitationHashAsync(hash, ct).ConfigureAwait(false);
            if (byToken is null || (requestId != Guid.Empty && byToken.Id != requestId))
            {
                return null;
            }

            return byToken;
        }

        if (requestId == Guid.Empty)
        {
            return null;
        }

        var request = await store.GetByIdAsync(requestId, ct).ConfigureAwait(false);
        return request is { Status: UserAssistanceStatus.Pending } && request.ExpiresAt > DateTimeOffset.UtcNow
            ? request
            : null;
    }

    private async Task<UserAssistanceSession?> FindActiveAssistanceConflictAsync(
        Guid userId,
        Guid userAccountId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = TenantFilterScope.BeginSystemScope();
        return await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.Status == UserAssistanceStatus.Active
                        && s.SessionExpiresAt > now
                        && (s.RequesterUserId == userId
                            || s.RequesterUserAccountId == userAccountId
                            || s.HelperUserId == userId
                            || s.HelperUserAccountId == userAccountId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasExternalElevatedContextAsync(
        Guid userId,
        Guid userAccountId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        using var scope = TenantFilterScope.BeginSystemScope();

        var hasImpersonation = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s => s.Status == UserImpersonationStatus.Active
                           && s.ExpiresAt > now
                           && (s.ActorUserId == userId
                               || s.ActorUserAccountId == userAccountId
                               || s.TargetUserId == userId
                               || s.TargetUserAccountId == userAccountId),
                ct)
            .ConfigureAwait(false);
        if (hasImpersonation)
        {
            return true;
        }

        return await db.TenantSupportAccessSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s => s.Status == SupportAccessStatus.Active
                           && s.ExpiresAt > now
                           && s.PlatformAdminUserAccountId == userAccountId,
                ct)
            .ConfigureAwait(false);
    }

    private async Task FailAsync(
        HttpContext context,
        UserAssistanceSession session,
        string reason,
        CancellationToken ct)
    {
        logger.LogWarning(
            "Invalidating assistance session {SessionId} for helper {Helper}: {Reason}",
            session.Id, session.HelperUsername, reason);

        metrics.ValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
        audit.Emit("user_assistance.validation_failed", new
        {
            session_id = session.Id.ToString(),
            requester_id = session.RequesterUserId.ToString(),
            helper_id = session.HelperUserId.ToString(),
            tenant_id = session.TenantId.ToString(),
            reason,
            path = context.Request.Path.Value
        });

        context.Session.Remove(SessionKeyName);
        context.Session.Remove(TenantSessionKeys.PreferredTenantId);
        context.Session.Remove(TenantSessionKeys.PreferredTenantSlug);

        try
        {
            var status = reason is "expired" or "target_session_invalid"
                ? UserAssistanceStatus.Expired
                : UserAssistanceStatus.Revoked;
            await store.EndAsync(session.Id, status, DateTimeOffset.UtcNow, null, reason, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not end invalid assistance session {SessionId}", session.Id);
        }
    }

    private static bool TryGetUserIds(ClaimsPrincipal principal, out Guid userId, out Guid userAccountId)
    {
        userId = ParseGuid(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? Guid.Empty;
        userAccountId = ParseGuid(principal.FindFirst(UserClaimTypes.UserAccountId)?.Value)
            ?? ParseGuid(principal.FindFirst(UserAssistanceClaimTypes.HelperUserAccountId)?.Value)
            ?? Guid.Empty;
        return userId != Guid.Empty && userAccountId != Guid.Empty;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static UserAssistanceInfo ToInfo(UserAssistanceSession session) => new()
    {
        SessionId = session.Id,
        TenantId = session.TenantId,
        RequesterUserId = session.RequesterUserId,
        RequesterUserAccountId = session.RequesterUserAccountId,
        RequesterUsername = session.RequesterUsername,
        RequesterDisplayName = session.RequesterDisplayName,
        HelperUserId = session.HelperUserId,
        HelperUserAccountId = session.HelperUserAccountId,
        HelperUsername = session.HelperUsername,
        HelperDisplayName = null,
        HelperEmail = session.HelperEmail,
        Mode = session.Mode,
        RightsMode = session.RightsMode,
        Status = session.Status,
        Reason = session.Reason,
        TicketReference = session.TicketReference,
        CreatedAt = session.CreatedAt,
        ExpiresAt = session.ExpiresAt,
        AcceptedAt = session.AcceptedAt,
        SessionExpiresAt = session.SessionExpiresAt,
        EndedAt = session.EndedAt,
        EndReason = session.EndReason
    };

    private string BuildAcceptLink(HttpContext context, Tenant tenant, Guid requestId, string token)
    {
        var baseUrl = oidcOptions.Value.PublicBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        }

        var tenantPrefix = multiTenancyOptions.Enabled
            ? $"/t/{Uri.EscapeDataString(tenant.Slug)}"
            : string.Empty;
        return $"{baseUrl}{tenantPrefix}/account/assistance/accept" +
               $"?requestId={requestId}&token={Uri.EscapeDataString(token)}";
    }

    private static EmailMessage BuildRequestEmail(
        UserAssistanceSession session,
        Tenant tenant,
        string acceptLink)
    {
        var requester = string.IsNullOrWhiteSpace(session.RequesterDisplayName)
            ? session.RequesterUsername
            : session.RequesterDisplayName;
        var helper = session.HelperUsername;
        var mode = session.Mode == UserAssistanceMode.ReadOnly ? "read-only" : "read-write";
        var rights = session.RightsMode == UserAssistanceRightsMode.FullTarget
            ? "full target rights"
            : "the intersection of your roles and the requester's roles";
        var subject = $"{requester} requested assistance in {tenant.Name}";
        var text = $"Hi {helper},\n\n" +
                   $"{requester} requested {mode} assistance in {tenant.Name}.\n\n" +
                   $"Reason: {session.Reason}\n" +
                   $"Rights: {rights}\n" +
                   $"Request expires: {session.ExpiresAt:MMM d, yyyy HH:mm 'UTC'}\n\n" +
                   $"Review and accept or decline the request:\n{acceptLink}\n\n" +
                   "The link only identifies the request. You must sign in as the invited user to accept it.";
        var html = $"<p>Hi {System.Net.WebUtility.HtmlEncode(helper)},</p>" +
                   $"<p><strong>{System.Net.WebUtility.HtmlEncode(requester)}</strong> requested " +
                   $"<strong>{mode}</strong> assistance in <strong>{System.Net.WebUtility.HtmlEncode(tenant.Name)}</strong>.</p>" +
                   $"<p><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(session.Reason)}<br>" +
                   $"<strong>Rights:</strong> {System.Net.WebUtility.HtmlEncode(rights)}<br>" +
                   $"<strong>Request expires:</strong> {session.ExpiresAt:MMM d, yyyy HH:mm 'UTC'}</p>" +
                   $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(acceptLink)}\" style=\"display:inline-block;padding:10px 18px;background-color:#0d6efd;color:#ffffff;text-decoration:none;border-radius:4px;\">Review request</a></p>" +
                   "<p><small>The link only identifies the request. You must sign in as the invited user to accept it.</small></p>";

        return new EmailMessage
        {
            To = new EmailAddress(session.HelperEmail ?? string.Empty, helper),
            Subject = subject,
            TextBody = text,
            HtmlBody = html
        };
    }

    private static EmailMessage BuildAcceptedEmail(
        UserAssistanceSession session,
        User requesterUser,
        UserAccount requesterAccount,
        Tenant tenant)
    {
        var requesterName = requesterUser.Name ?? requesterAccount.Name ?? requesterUser.Username;
        return new EmailMessage
        {
            To = new EmailAddress(requesterUser.Email ?? requesterAccount.Email ?? string.Empty, requesterName),
            Subject = $"{session.HelperUsername} accepted your assistance request",
            TextBody = $"Hi {requesterName},\n\n" +
                       $"{session.HelperUsername} accepted your assistance request in {tenant.Name}.\n" +
                       $"The session is active until {session.SessionExpiresAt:MMM d, yyyy HH:mm 'UTC'}.\n\n" +
                       "You can end the session at any time from Account > Assistance.",
            HtmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(requesterName)},</p>" +
                       $"<p><strong>{System.Net.WebUtility.HtmlEncode(session.HelperUsername)}</strong> accepted your assistance request in " +
                       $"<strong>{System.Net.WebUtility.HtmlEncode(tenant.Name)}</strong>.</p>" +
                       $"<p>The session is active until {session.SessionExpiresAt:MMM d, yyyy HH:mm 'UTC'}. " +
                       "You can end it at any time from <strong>Account &gt; Assistance</strong>.</p>"
        };
    }

    private static EmailMessage BuildDeclinedEmail(UserAssistanceSession session, User requesterUser)
    {
        var requesterName = requesterUser.Name ?? requesterUser.Username;
        return new EmailMessage
        {
            To = new EmailAddress(requesterUser.Email ?? string.Empty, requesterName),
            Subject = $"{session.HelperUsername} declined your assistance request",
            TextBody = $"Hi {requesterName},\n\n{session.HelperUsername} declined your assistance request.",
            HtmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(requesterName)},</p>" +
                       $"<p><strong>{System.Net.WebUtility.HtmlEncode(session.HelperUsername)}</strong> declined your assistance request.</p>"
        };
    }

    private static EmailMessage BuildCancelledEmail(UserAssistanceSession session, User helperUser)
    {
        var helperName = helperUser.Name ?? helperUser.Username;
        return new EmailMessage
        {
            To = new EmailAddress(helperUser.Email ?? string.Empty, helperName),
            Subject = "Assistance request cancelled",
            TextBody = $"Hi {helperName},\n\nThe assistance request from {session.RequesterUsername} was cancelled.",
            HtmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(helperName)},</p>" +
                       $"<p>The assistance request from <strong>{System.Net.WebUtility.HtmlEncode(session.RequesterUsername)}</strong> was cancelled.</p>"
        };
    }

    private static EmailMessage BuildEndedEmail(
        UserAssistanceSession session,
        User recipient,
        string recipientRole,
        UserAssistanceStatus status)
    {
        var name = recipient.Name ?? recipient.Username;
        var ending = status == UserAssistanceStatus.Revoked ? "revoked" : "ended";
        return new EmailMessage
        {
            To = new EmailAddress(recipient.Email ?? string.Empty, name),
            Subject = $"Assistance session {ending}",
            TextBody = $"Hi {name},\n\nThe assistance session between {session.RequesterUsername} and " +
                       $"{session.HelperUsername} was {ending}.",
            HtmlBody = $"<p>Hi {System.Net.WebUtility.HtmlEncode(name)},</p>" +
                       $"<p>The assistance session between <strong>{System.Net.WebUtility.HtmlEncode(session.RequesterUsername)}</strong> and " +
                       $"<strong>{System.Net.WebUtility.HtmlEncode(session.HelperUsername)}</strong> was {ending}.</p>"
        };
    }

    private async Task SendNotificationAsync(EmailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.To.Email))
        {
            logger.LogDebug("Assistance notification skipped: recipient has no email address");
            return;
        }

        try
        {
            await emailSender.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Email is optional: the in-app inbox remains the primary channel.
            logger.LogWarning(ex, "Assistance notification could not be sent to {Recipient}", message.To.Email);
        }
    }
}
