using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization handler for platform administrators.
/// Checks if the user has the 'platform-admin' role in the designated platform realm.
/// </summary>
public sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly AuthDbContext _db;
    private readonly Microsoft.Extensions.Options.IOptions<PlatformAdminAuthOptions> _options;
    private readonly IDefaultTenantContext _defaultTenantContext;

    public PlatformAdminAuthorizationHandler(
        AuthDbContext db,
        Microsoft.Extensions.Options.IOptions<PlatformAdminAuthOptions> options,
        IDefaultTenantContext defaultTenantContext)
    {
        _db = db;
        _options = options;
        _defaultTenantContext = defaultTenantContext;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return;

        var realmName = _options.Value.RealmName;
        var roleName = _options.Value.PlatformAdminRoleName;
        var platformTenantId = await _defaultTenantContext.GetDefaultTenantIdAsync().ConfigureAwait(false);
        if (platformTenantId is null)
            return;

        // Check if user has the platform-admin role in the platform realm (realm-scoped)
        var hasRole = await _db.UserRealmRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId
                           && x.a.IsActive
                           && x.a.RealmId == x.rl.Id
                           && x.r.IsActive
                           && x.r.TenantId == platformTenantId.Value
                           && x.r.Name == roleName
                           && x.rl.TenantId == platformTenantId.Value
                           && x.rl.Name == realmName);

        if (hasRole)
            context.Succeed(requirement);
    }
}
