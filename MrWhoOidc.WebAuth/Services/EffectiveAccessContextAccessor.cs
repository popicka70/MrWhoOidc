using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Services.SupportAccess;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Resolves an immutable EffectiveAccessContext for the current request.
/// Evaluates in priority order:
/// 1. Tenant Support Access session (from HttpContext.Session["SupportAccessSessionId"])
/// 2. Delegated Access Grant (from HttpContext.Session["DelegatedAccessGrantId"])
/// 3. Normal fallback (actor = subject, tenant from claims or ITenantAccessor)
/// 
/// Implements AD-1: Keep actor and subject distinct.
/// Thread-safe: uses only local state and the provided CancellationToken.
/// </summary>
internal sealed class EffectiveAccessContextAccessor(
    IHttpContextAccessor httpContextAccessor,
    ITenantSupportAccessStore supportAccessStore,
    IDelegatedAccessAuthorizationService delegatedAccessAuthorizationService,
    IUserAccountService userAccountService,
    AuthDbContext dbContext,
    ILogger<EffectiveAccessContextAccessor> logger)
    : IEffectiveAccessContextAccessor
{
    private const string SupportAccessSessionIdKey = "SupportAccessSessionId";
    private const string DelegatedAccessGrantIdKey = "DelegatedAccessGrantId";

    /// <summary>
    /// Resolve the current effective access context.
    /// Priority order: Tenant Support Access > Delegated Access > Normal.
    /// </summary>
    /// <param name="ct">Cancellation token forwarded to all async dependencies.</param>
    /// <returns>The resolved EffectiveAccessContext.</returns>
    /// <exception cref="AuthorizationError">If the authenticated user cannot be resolved or tenant context is missing for normal access.</exception>
    public async Task<EffectiveAccessContext> GetContextAsync(CancellationToken ct = default)
    {
        // Step 0: Resolve current authenticated user's UserAccountId from claims
        var httpContext = httpContextAccessor.HttpContext;
        var userIdClaim = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var actorUserAccountId))
        {
            throw new AuthorizationError("Authenticated user account ID not resolvable from claims.");
        }

        // Step 1: Check for an active Tenant Support Access session
        var supportSessionIdStr = httpContext.Session.GetString(SupportAccessSessionIdKey);
        if (!string.IsNullOrWhiteSpace(supportSessionIdStr))
        {
            if (!Guid.TryParse(supportSessionIdStr, out var sessionId))
            {
                httpContext.Session.Remove(SupportAccessSessionIdKey);
                logger.LogWarning("EffectiveAccessContext: Malformed support access session reference cleared.");
                sessionId = Guid.Empty;
            }

            // Load the durable session directly from DB to verify invariants.
            // ITenantSupportAccessStore.GetByIdAsync requires a tenantId parameter,
            // which is not known until we load the session. Use direct DB lookup instead.
            var session = await dbContext.TenantSupportAccessSessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (session is not null
            && session.Status == SupportAccessStatus.Active
            && session.ExpiresAt > DateTimeOffset.UtcNow
            && session.PlatformAdminUserAccountId == actorUserAccountId)
            {
                // Valid Tenant Support Access context — return immediately.
                // Per AD-1: SubjectUserAccountId is null (no user subject).
                logger.LogInformation(
                "EffectiveAccessContext: TenantSupportAccess for tenant {TenantId}, session {SessionId}",
                session.TenantId, sessionId);

                return new EffectiveAccessContext(
                ActorUserAccountId: actorUserAccountId,
                SubjectUserAccountId: Guid.Empty,  // No user subject per AD-1
                TenantId: session.TenantId,
                Kind: AccessContextKind.TenantSupportAccess,
                SupportAccessSessionId: sessionId,
                DelegatedAccessGrantId: null);
            }

            // Session invalid — clear stale reference from HTTP session
            logger.LogWarning(
                "EffectiveAccessContext: Stale/invalid support access session {SessionId} cleared (status={Status}, expires={ExpiresAt}, actor={ActorId})",
                sessionId, session?.Status, session?.ExpiresAt, actorUserAccountId);
            httpContext.Session.Remove(SupportAccessSessionIdKey);
        }

        // Step 2: Check for an active Delegated Access Grant
        var grantIdStr = httpContext.Session.GetString(DelegatedAccessGrantIdKey);
        if (!string.IsNullOrWhiteSpace(grantIdStr))
        {
            if (!Guid.TryParse(grantIdStr, out var grantId))
            {
                httpContext.Session.Remove(DelegatedAccessGrantIdKey);
                logger.LogWarning("EffectiveAccessContext: Malformed delegated access grant reference cleared.");
                grantId = Guid.Empty;
            }

            // Validate the grant's basic invariants via direct DB lookup:
            // active status, valid time window, and actor matches delegate.
            var grant = await dbContext.DelegatedAccessGrants
                .AsNoTracking()
                .Where(g => g.Id == grantId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (grant is not null
            && grant.Status == DelegatedAccessGrantStatus.Active
            && (grant.StartsAt is null || grant.StartsAt <= DateTimeOffset.UtcNow)
            && grant.ExpiresAt > DateTimeOffset.UtcNow
            && grant.DelegateUserAccountId == actorUserAccountId)
            {
                // Valid Delegated Access context — return immediately.
                // Per AD-1: Actor = delegate, Subject = delegator.
                logger.LogInformation(
                "EffectiveAccessContext: DelegatedAccess grant {GrantId} for delegator {DelegatorId}",
                grantId, grant.DelegatorUserAccountId);

                return new EffectiveAccessContext(
                ActorUserAccountId: actorUserAccountId,
                SubjectUserAccountId: grant.DelegatorUserAccountId,
                TenantId: grant.TenantId,
                Kind: AccessContextKind.DelegatedAccess,
                SupportAccessSessionId: null,
                DelegatedAccessGrantId: grantId);
            }

            // Grant invalid — clear stale reference
            logger.LogWarning(
                "EffectiveAccessContext: Stale/invalid delegated grant {GrantId} cleared (status={Status}, expires={ExpiresAt}, delegate={DelegateId})",
                grantId, grant?.Status, grant?.ExpiresAt, actorUserAccountId);
            httpContext.Session.Remove(DelegatedAccessGrantIdKey);
        }

        // Step 3: Fallback — Normal Access
        var tenantClaim = httpContext.User?.FindFirstValue(OidcConstants.Claims.TenantId);
        if (tenantClaim is not null && Guid.TryParse(tenantClaim, out var tenantIdFromClaim))
        {
            logger.LogInformation(
                "EffectiveAccessContext: Normal access for actor {ActorId}, tenant {TenantId}",
                actorUserAccountId, tenantIdFromClaim);

            return new EffectiveAccessContext(
                ActorUserAccountId: actorUserAccountId,
                SubjectUserAccountId: actorUserAccountId,
                TenantId: tenantIdFromClaim,
                Kind: AccessContextKind.Normal,
                SupportAccessSessionId: null,
                DelegatedAccessGrantId: null);
        }

        var tenantAccessor = httpContext.RequestServices.GetService<ITenantAccessor>();
        if (tenantAccessor?.CurrentTenant is not null)
        {
            logger.LogInformation(
                    "EffectiveAccessContext: Normal access for actor {ActorId}, tenant {TenantId} (from ITenantAccessor)",
                actorUserAccountId, tenantAccessor.CurrentTenant.TenantId);

            return new EffectiveAccessContext(
                ActorUserAccountId: actorUserAccountId,
                SubjectUserAccountId: actorUserAccountId,
                TenantId: tenantAccessor.CurrentTenant.TenantId,
                Kind: AccessContextKind.Normal,
                SupportAccessSessionId: null,
                DelegatedAccessGrantId: null);
        }

        throw new AuthorizationError("Cannot resolve tenant context for normal access.");
    }
}
