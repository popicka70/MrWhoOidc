using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    private readonly AuthDbContext _db;
    private readonly Microsoft.Extensions.Options.IOptions<AdminAuthOptions> _options;

    public AdminAuthorizationHandler(AuthDbContext db, Microsoft.Extensions.Options.IOptions<AdminAuthOptions> options)
    {
        _db = db;
        _options = options;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return;

        var realmName = _options.Value.RealmName;
        var roleName = _options.Value.AdminRoleName;

        var hasAdmin = await _db.UserRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId && x.a.IsActive && x.r.IsActive
                           && x.r.Name == roleName && x.rl.Name == realmName);

        if (hasAdmin)
            context.Succeed(requirement);
    }
}
