using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;

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
        var resolved = await userAccountResolver.ResolveAsync(user);
        if (resolved is null)
        {
            return new List<TenantAccessInfo>();
        }

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
            from assignment in db.UserRoleAssignments.AsNoTracking()
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
        if (httpContext is null)
        {
            throw new ArgumentNullException(nameof(httpContext));
        }

        httpContext.Session.SetString(TenantSessionKeys.PreferredTenantId, tenantId.ToString());

        var tenantSlug = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(httpContext.RequestAborted);

        if (!string.IsNullOrEmpty(tenantSlug))
        {
            httpContext.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenantSlug);
        }
        else
        {
            httpContext.Session.Remove(TenantSessionKeys.PreferredTenantSlug);
        }

        await ReissueAuthenticationAsync(httpContext, tenantId);
    }

    public Guid? GetPreferredTenantId(HttpContext httpContext)
    {
        var tenantIdStr = httpContext.Session.GetString(TenantSessionKeys.PreferredTenantId);
        if (Guid.TryParse(tenantIdStr, out var tenantId))
        {
            return tenantId;
        }
        return null;
    }

    public string? GetPreferredTenantSlug(HttpContext httpContext)
    {
        return httpContext.Session.GetString(TenantSessionKeys.PreferredTenantSlug);
    }

    private async Task<List<TenantAccessInfo>> GetLegacyTenantsAsync(Guid userId)
    {
        var tenantAccess = await db.UserRoleAssignments
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
        if (!(httpContext.User?.Identity?.IsAuthenticated ?? false))
        {
            return;
        }

        var tenantInfos = await GetUserTenantsAsync(httpContext.User);
        var targetTenant = tenantInfos.FirstOrDefault(t => t.TenantId == tenantId);
        if (targetTenant is null)
        {
            logger.LogWarning("Tenant switch requested for tenant {TenantId} but user does not have access", tenantId);
            return;
        }

        if (targetTenant.TenantUserId == Guid.Empty)
        {
            logger.LogWarning("No tenant-specific user id resolved for tenant {TenantId}", tenantId);
            return;
        }

        var tenantUser = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetTenant.TenantUserId, httpContext.RequestAborted);

        if (tenantUser is null)
        {
            logger.LogWarning("Tenant-specific user {UserId} not found for tenant {TenantId}", targetTenant.TenantUserId, tenantId);
            return;
        }

        var resolution = await userAccountResolver.ResolveAsync(httpContext.User, httpContext.RequestAborted);
        var accountId = resolution?.UserAccountId ?? resolution?.UserId;

        var existingAuth = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var props = existingAuth?.Properties ?? new AuthenticationProperties();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantUser.Id.ToString()),
            new(ClaimTypes.Name, tenantUser.Username),
            new("auth_time", httpContext.User.FindFirst("auth_time")?.Value ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (accountId.HasValue)
        {
            claims.Add(new(UserClaimTypes.UserAccountId, accountId.Value.ToString()));
        }

        if (!string.IsNullOrEmpty(tenantUser.Email))
        {
            claims.Add(new(ClaimTypes.Email, tenantUser.Email));
        }

        var idpClaim = httpContext.User.FindFirst("idp")?.Value;
        if (!string.IsNullOrEmpty(idpClaim))
        {
            claims.Add(new("idp", idpClaim));
        }

        var amrClaims = httpContext.User.FindAll("amr").ToList();
        if (amrClaims.Count > 0)
        {
            foreach (var amr in amrClaims)
            {
                claims.Add(new("amr", amr.Value));
            }
        }
        else
        {
            claims.Add(new("amr", "tenant_switch"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
        logger.LogInformation("User switched to tenant {TenantId} with subject {UserId}", tenantId, tenantUser.Id);
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
