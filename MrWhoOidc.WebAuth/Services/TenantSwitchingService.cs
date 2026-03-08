using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Infrastructure.Logging;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for managing tenant switching and checking user's tenant access
/// </summary>
public interface ITenantSwitchingService
{
    /// <summary>
    /// Get all tenants the current user has access to (has roles in)
    /// </summary>
    Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user);

    /// <summary>
    /// Switch the user's current tenant context (stores in session)
    /// </summary>
    Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId);

    /// <summary>
    /// Get the user's preferred tenant from session
    /// </summary>
    Guid? GetPreferredTenantId(HttpContext httpContext);

    /// <summary>
    /// Get the user's preferred tenant slug from session
    /// </summary>
    string? GetPreferredTenantSlug(HttpContext httpContext);
}

public class TenantSwitchingService(
    AuthDbContext db,
    ICurrentUserAccountResolver userAccountResolver,
    ILogger<TenantSwitchingService> logger) : ITenantSwitchingService
{

    public async Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user)
    {
        logger.LogDebug("🔍 [GetUserTenants] START - Resolving tenants for user");

        var resolved = await userAccountResolver.ResolveAsync(user);
        if (resolved is null)
        {
            logger.LogWarning("🔍 [GetUserTenants] User account could not be resolved");
            return new List<TenantAccessInfo>();
        }

        logger.LogDebug("🔍 [GetUserTenants] Resolved: UserId={UserId}, UserAccountId={UserAccountId}, Email={Email}",
            resolved.Value.UserId, resolved.Value.UserAccountId, resolved.Value.NormalizedEmail);

        var normalizedEmail = resolved.Value.NormalizedEmail;

        if (string.IsNullOrEmpty(normalizedEmail) && resolved.Value.UserAccountId is Guid userAccountId)
        {
            var accountEmail = await db.UserAccounts.AsNoTracking()
                .Where(a => a.Id == userAccountId)
                .Select(a => a.NormalizedEmail ?? a.Email)
                .FirstOrDefaultAsync();
            normalizedEmail = EmailNormalizer.NormalizeForLookup(accountEmail);
        }

        if (string.IsNullOrEmpty(normalizedEmail))
        {
            var userEmail = await db.Users.AsNoTracking()
                .Where(u => u.Id == resolved.Value.UserId)
                .Select(u => u.NormalizedEmail ?? u.Email)
                .FirstOrDefaultAsync();
            normalizedEmail = EmailNormalizer.NormalizeForLookup(userEmail);
        }

        if (string.IsNullOrEmpty(normalizedEmail))
        {
            return await GetLegacyTenantsAsync(resolved.Value.UserId);
        }

        var userTenantRows = await (
            from legacyUser in db.Users.AsNoTracking()
            where legacyUser.NormalizedEmail == normalizedEmail
            join tenant in db.Tenants.AsNoTracking() on legacyUser.TenantId equals tenant.Id
            where tenant.Status == TenantStatus.Active
            select new UserTenantRow(
                legacyUser.Id,
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.IssuerUri)
        ).ToListAsync();

        if (userTenantRows.Count == 0)
        {
            return await GetLegacyTenantsAsync(resolved.Value.UserId);
        }

        var userIds = userTenantRows.Select(x => x.UserId).Distinct().ToList();

        var roleAssignments = await (
            from assignment in db.UserRealmRoleAssignments.AsNoTracking()
            where assignment.IsActive && userIds.Contains(assignment.UserId)
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where role.IsActive
            select new { assignment.UserId, role.Name }
        ).ToListAsync();

        var rolesByUser = roleAssignments.ToLookup(x => x.UserId, x => x.Name);

        var adminTenantIds = resolved.Value.UserAccountId is Guid membershipAccountId
            ? (await db.UserTenantMemberships.AsNoTracking()
                .Where(m => m.UserAccountId == membershipAccountId
                            && m.Status == TenantMembershipStatus.Active
                            && m.IsTenantAdmin)
                .Select(m => m.TenantId)
                .ToListAsync())
                .ToHashSet()
            : new HashSet<Guid>();

        var tenants = userTenantRows
            .GroupBy(x => new { x.TenantId, x.TenantName, x.TenantSlug, x.IssuerUri })
            .Select(g =>
            {
                var roleNames = g.SelectMany(entry => rolesByUser[entry.UserId])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var hasAdminRole = roleNames.Any(role =>
                    string.Equals(role, "platform-admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "tenant-admin", StringComparison.OrdinalIgnoreCase));

                return new TenantAccessInfo
                {
                    TenantId = g.Key.TenantId,
                    TenantName = g.Key.TenantName,
                    TenantSlug = g.Key.TenantSlug,
                    IssuerUri = g.Key.IssuerUri,
                    TenantUserId = g.Select(entry => entry.UserId).FirstOrDefault(),
                    HasAdminAccess = hasAdminRole || adminTenantIds.Contains(g.Key.TenantId),
                    RoleCount = roleNames.Count
                };
            })
            .OrderBy(t => t.TenantName)
            .ToList();

        return tenants;
    }

    public async Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId)
    {
        logger.LogInformation("🔀 [SwitchTenant] START - Switching to tenant {TenantId}", tenantId);

        if (httpContext is null)
        {
            throw new ArgumentNullException(nameof(httpContext));
        }

        httpContext.Session.SetString(TenantSessionKeys.PreferredTenantId, tenantId.ToString());
        logger.LogDebug("🔀 [SwitchTenant] Set session PreferredTenantId={TenantId}", tenantId);

        var tenantSlug = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(httpContext.RequestAborted);

        if (!string.IsNullOrEmpty(tenantSlug))
        {
            httpContext.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenantSlug);
            logger.LogDebug("🔀 [SwitchTenant] Set session PreferredTenantSlug={TenantSlug}", tenantSlug);
        }
        else
        {
            httpContext.Session.Remove(TenantSessionKeys.PreferredTenantSlug);
            logger.LogWarning("🔀 [SwitchTenant] Tenant {TenantId} not found in database - cleared slug from session", tenantId);
        }

        await ReissueAuthenticationAsync(httpContext, tenantId);
        logger.LogInformation("🔀 [SwitchTenant] COMPLETE - Authentication reissued for tenant {TenantId}", tenantId);
    }

    public Guid? GetPreferredTenantId(HttpContext httpContext)
    {
        try
        {
            var sessionId = httpContext.Session?.Id;
            logger.LogInformation("[GetPreferredTenantId] Session ID: {SessionId}, IsAvailable: {IsAvailable}",
                sessionId ?? "(no session)", httpContext.Session != null);

            var tenantIdStr = httpContext.Session?.GetString(TenantSessionKeys.PreferredTenantId);
            logger.LogInformation("[GetPreferredTenantId] Raw session value for PreferredTenantId: {Value}", tenantIdStr ?? "(null)");

            if (Guid.TryParse(tenantIdStr, out var tenantId))
            {
                logger.LogInformation("[GetPreferredTenantId] Returning tenant ID: {TenantId}", tenantId);
                return tenantId;
            }

            logger.LogInformation("[GetPreferredTenantId] No valid tenant ID in session");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GetPreferredTenantId] Exception accessing session");
            return null;
        }
    }

    public string? GetPreferredTenantSlug(HttpContext httpContext)
    {
        try
        {
            var slug = httpContext.Session?.GetString(TenantSessionKeys.PreferredTenantSlug);
            logger.LogDebug("[GetPreferredTenantSlug] Session slug: {Slug}", slug ?? "(null)");
            return slug;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GetPreferredTenantSlug] Exception accessing session");
            return null;
        }
    }

    private async Task<List<TenantAccessInfo>> GetLegacyTenantsAsync(Guid userId)
    {
        var tenantAccess = await db.UserRealmRoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.IsActive)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .Join(db.Tenants, x => x.rl.TenantId, t => t.Id, (x, t) => new { x.a, x.r, x.rl, t })
            .Where(x => x.t.Status == TenantStatus.Active)
            .Select(x => new
            {
                TenantId = x.t.Id,
                TenantName = x.t.Name,
                TenantSlug = x.t.Slug,
                IssuerUri = x.t.IssuerUri,
                RoleName = x.r.Name,
                x.a.UserId
            })
            .ToListAsync();

        return tenantAccess
            .GroupBy(x => new { x.TenantId, x.TenantName, x.TenantSlug, x.IssuerUri })
            .Select(g => new TenantAccessInfo
            {
                TenantId = g.Key.TenantId,
                TenantName = g.Key.TenantName,
                TenantSlug = g.Key.TenantSlug,
                IssuerUri = g.Key.IssuerUri,
                TenantUserId = g.Select(x => x.UserId).FirstOrDefault(),
                HasAdminAccess = g.Any(x => x.RoleName == "platform-admin" || x.RoleName == "tenant-admin"),
                RoleCount = g.Select(x => x.RoleName).Distinct().Count()
            })
            .OrderBy(t => t.TenantName)
            .ToList();
    }

    private async Task ReissueAuthenticationAsync(HttpContext httpContext, Guid tenantId)
    {
        logger.LogDebug("🔑 [ReissueAuth] START - Reissuing auth for tenant {TenantId}", tenantId);

        if (!(httpContext.User?.Identity?.IsAuthenticated ?? false))
        {
            logger.LogWarning("🔑 [ReissueAuth] User not authenticated - skipping");
            return;
        }

        var tenantInfos = await GetUserTenantsAsync(httpContext.User);
        logger.LogDebug("🔑 [ReissueAuth] Found {Count} accessible tenants", tenantInfos.Count);

        var targetTenant = tenantInfos.FirstOrDefault(t => t.TenantId == tenantId);
        if (targetTenant is null)
        {
            logger.LogWarning("🔑 [ReissueAuth] Tenant switch requested for tenant {TenantId} but user does not have access", tenantId);
            return;
        }

        logger.LogDebug("🔑 [ReissueAuth] Target tenant: {TenantName}, TenantUserId={TenantUserId}",
            targetTenant.TenantName, targetTenant.TenantUserId);

        if (targetTenant.TenantUserId == Guid.Empty)
        {
            logger.LogWarning("🔑 [ReissueAuth] No tenant-specific user id resolved for tenant {TenantId} - TenantUserId is empty!", tenantId);
            return;
        }

        var tenantUser = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetTenant.TenantUserId, httpContext.RequestAborted);

        if (tenantUser is null)
        {
            logger.LogWarning("🔑 [ReissueAuth] Tenant-specific user {UserId} not found in Users table for tenant {TenantId}", targetTenant.TenantUserId, tenantId);
            return;
        }

        logger.LogDebug("🔑 [ReissueAuth] Found tenant user: Username={Username}, Email={Email}, TenantId={UserTenantId}",
            tenantUser.Username, tenantUser.Email, tenantUser.TenantId);

        var resolution = await userAccountResolver.ResolveAsync(httpContext.User, httpContext.RequestAborted);
        var accountId = resolution?.UserAccountId ?? resolution?.UserId;

        var existingAuth = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var props = existingAuth?.Properties ?? new AuthenticationProperties();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantUser.Id.ToString()),
            new(ClaimTypes.Name, tenantUser.Username),
            new(OidcConstants.Claims.AuthTime, httpContext.User.FindFirst(OidcConstants.Claims.AuthTime)?.Value ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (accountId.HasValue)
        {
            claims.Add(new(UserClaimTypes.UserAccountId, accountId.Value.ToString()));
        }

        if (!string.IsNullOrEmpty(tenantUser.Email))
        {
            claims.Add(new(ClaimTypes.Email, tenantUser.Email));
        }

        var idpClaim = httpContext.User.FindFirst(OidcConstants.Claims.Idp)?.Value;
        if (!string.IsNullOrEmpty(idpClaim))
        {
            claims.Add(new(OidcConstants.Claims.Idp, idpClaim));
        }

        var acrClaim = httpContext.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
        if (!string.IsNullOrEmpty(acrClaim))
        {
            claims.Add(new(OidcConstants.Claims.Acr, acrClaim));
        }

        var amrClaims = httpContext.User.FindAll(OidcConstants.Claims.Amr).ToList();
        if (amrClaims.Count > 0)
        {
            foreach (var amr in amrClaims)
            {
                claims.Add(new(OidcConstants.Claims.Amr, amr.Value));
            }
        }
        else
        {
            claims.Add(new(OidcConstants.Claims.Amr, "tenant_switch"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
        logger.LogInformation("🔑 [ReissueAuth] SUCCESS - User signed in with new identity. SubjectHash={SubjectHash}, TenantId={TenantId}, Claims={ClaimCount}",
            LogTokenization.HashId(tenantUser.Id.ToString()), tenantId, claims.Count);
    }

    private sealed record UserTenantRow(Guid UserId, Guid TenantId, string TenantName, string TenantSlug, string IssuerUri);
}

public class TenantAccessInfo
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public string IssuerUri { get; set; } = string.Empty;
    public Guid TenantUserId { get; set; }
    public bool HasAdminAccess { get; set; }
    public int RoleCount { get; set; }
}
