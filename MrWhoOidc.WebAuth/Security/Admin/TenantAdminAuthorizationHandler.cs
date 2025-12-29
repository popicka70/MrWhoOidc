using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization handler for tenant admin access.
/// Checks if the user has the tenant-admin role in the current tenant's default realm.
/// Platform admins must use impersonation to access tenant admin functions.
/// </summary>
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantSwitchingService _tenantSwitchingService;
    private readonly IOptions<TenantAdminAuthOptions> _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantAdminAuthorizationHandler(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        ITenantSwitchingService tenantSwitchingService,
        IOptions<TenantAdminAuthOptions> options,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _tenantSwitchingService = tenantSwitchingService;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAdminRequirement requirement)
    {
        // Get user ID from claims
        var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return;

        var httpContext = _httpContextAccessor.HttpContext;

        // Helper: Get effective tenant ID (middleware-resolved or session fallback)
        Guid? GetEffectiveTenantId()
        {
            var tid = _tenantAccessor.CurrentTenant?.TenantId;
            if (tid == null && httpContext != null)
            {
                tid = _tenantSwitchingService.GetPreferredTenantId(httpContext);
            }
            return tid;
        }

        // Check if platform admin is impersonating this tenant
        // Access session directly to avoid circular dependency with IImpersonationService
        if (httpContext?.Session != null)
        {
            var impersonatedTenantIdStr = httpContext.Session.GetString("ImpersonatingTenantId");
            if (!string.IsNullOrEmpty(impersonatedTenantIdStr) && Guid.TryParse(impersonatedTenantIdStr, out var impersonatedTenantId))
            {
                var currentTenantId = GetEffectiveTenantId();

                if (impersonatedTenantId == currentTenantId)
                {
                    // User is a platform admin impersonating this tenant - grant access
                    context.Succeed(requirement);
                    return;
                }
            }
        }

        // Get current tenant context - try middleware first, then session fallback
        // This ensures authorization works even on pages that skip tenant resolution (e.g., /platform-admin/*)
        var tenantId = GetEffectiveTenantId();
        
        if (tenantId == null)
        {
            // No tenant context - cannot proceed
            // This can happen if middleware hasn't run yet or tenant resolution failed
            return;
        }

        // Check if user has tenant-admin role in current tenant's default realm (realm-scoped)
        var realmName = _options.Value.RealmName;
        var roleName = _options.Value.TenantAdminRoleName;

        var hasRole = await _db.UserRealmRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId
                           && x.a.IsActive
                           && x.r.IsActive
                           && x.r.Name == roleName
                           && x.rl.TenantId == tenantId
                           && x.rl.Name == realmName);

        if (hasRole)
            context.Succeed(requirement);
    }
}
