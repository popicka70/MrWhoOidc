using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization handler for tenant admin access.
/// Checks if the user has the tenant-admin role in the current tenant's default realm.
/// Platform admins automatically satisfy this requirement.
/// </summary>
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IOptions<TenantAdminAuthOptions> _options;
    private readonly IOptions<PlatformAdminAuthOptions> _platformOptions;

    public TenantAdminAuthorizationHandler(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IOptions<TenantAdminAuthOptions> options,
        IOptions<PlatformAdminAuthOptions> platformOptions)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _options = options;
        _platformOptions = platformOptions;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAdminRequirement requirement)
    {
        // Get user ID from claims
        var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return;

        // Check if user is platform admin - platform admins have access to all tenant admin functions
        var platformRealmName = _platformOptions.Value.RealmName;
        var platformRoleName = _platformOptions.Value.PlatformAdminRoleName;

        var isPlatformAdmin = await _db.UserRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId
                           && x.a.IsActive
                           && x.r.IsActive
                           && x.r.Name == platformRoleName
                           && x.rl.Name == platformRealmName);

        if (isPlatformAdmin)
        {
            context.Succeed(requirement);
            return;
        }

        // Get current tenant context
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId == null)
        {
            // No tenant context - cannot proceed
            // This can happen if middleware hasn't run yet or tenant resolution failed
            return;
        }

        // Check if user has tenant-admin role in current tenant's default realm
        var realmName = _options.Value.RealmName;
        var roleName = _options.Value.TenantAdminRoleName;

        var isTenantAdmin = await _db.UserRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId
                           && x.a.IsActive
                           && x.r.IsActive
                           && x.r.Name == roleName
                           && x.rl.TenantId == tenantId
                           && x.rl.Name == realmName);

        if (isTenantAdmin)
            context.Succeed(requirement);
    }
}
