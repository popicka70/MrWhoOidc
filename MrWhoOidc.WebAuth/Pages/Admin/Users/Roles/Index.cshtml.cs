using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Roles;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : UserPageModelBase(tenantAccessor)
{
    [FromRoute]
    public Guid UserId { get; set; }

    // Add (realm-role)
    [BindProperty]
    public Guid RealmAddRealmId { get; set; }
    [BindProperty]
    public Guid RealmAddRoleId { get; set; }
    [BindProperty]
    public bool RealmIsActive { get; set; } = true;

    // Add (client-role)
    [BindProperty]
    public Guid ClientAddClientId { get; set; }
    [BindProperty]
    public Guid ClientAddRoleId { get; set; }
    [BindProperty]
    public bool ClientIsActive { get; set; } = true;

    public string TenantName { get; set; } = string.Empty;
    public Guid UserTenantId { get; set; }

    public IReadOnlyList<RealmAssignmentVm> RealmAssignments { get; private set; } = Array.Empty<RealmAssignmentVm>();
    public IReadOnlyList<ClientAssignmentVm> ClientAssignments { get; private set; } = Array.Empty<ClientAssignmentVm>();

    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<RealmVm> Realms { get; private set; } = Array.Empty<RealmVm>();
    public IReadOnlyList<RoleVm> Roles { get; private set; } = Array.Empty<RoleVm>();

    public record RealmAssignmentVm(Guid RoleId, Guid RealmId, string RealmName, string RoleName, string TenantName, bool IsActive);
    public record ClientAssignmentVm(Guid RoleId, Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, string RoleName, string TenantName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string? ClientName);
    public record RoleVm(Guid Id, string Name, string RealmName, Guid RealmId);
    public record RealmVm(Guid Id, string Name);

    public async Task<IActionResult> OnGetAsync()
    {
        var userQuery = from u in db.Users.AsNoTracking()
                        join t in db.Tenants on u.TenantId equals t.Id
                        where u.Id == UserId
                        select new { User = u, Tenant = t };

        var userResult = await userQuery.FirstOrDefaultAsync();
        if (userResult is null) return RedirectToPage("/Admin/Users/Index");

        UserTenantId = userResult.User.TenantId;
        TenantName = userResult.Tenant.Name;
        SetHeading(userResult.User.Username, userResult.User.Name);

        // Realms filtered by user's tenant
        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == UserTenantId)
            .OrderBy(r => r.Name)
            .Select(r => new RealmVm(r.Id, r.Name))
            .ToListAsync();

        // Clients filtered by user's tenant
        Clients = await db.Clients.AsNoTracking()
            .Where(c => c.TenantId == UserTenantId)
            .OrderBy(c => c.ClientId)
            .Select(c => new ClientVm(c.Id, c.ClientId, c.ClientName))
            .ToListAsync();

        // Roles filtered by user's tenant with their realm name
        Roles = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == UserTenantId)
            .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new { RoleId = r.Id, r.Name, RealmName = rl.Name, RealmId = rl.Id })
            .OrderBy(x => x.Name)
            .Select(x => new RoleVm(x.RoleId, x.Name, x.RealmName, x.RealmId))
            .ToListAsync();

        // Realm-role assignments with tenant info
        RealmAssignments = await db.UserRealmRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms, ar => ar.a.RealmId, rl => rl.Id, (ar, rl) => new { ar, rl })
            .Join(db.Tenants, arrl => arrl.ar.r.TenantId, t => t.Id, (arrl, t) => new { arrl.ar, arrl.rl, t })
            .OrderBy(x => x.rl.Name).ThenBy(x => x.ar.r.Name)
            .Select(x => new RealmAssignmentVm(x.ar.r.Id, x.rl.Id, x.rl.Name, x.ar.r.Name, x.t.Name, x.ar.a.IsActive))
            .ToListAsync();

        // Client-role assignments with tenant info
        ClientAssignments = await db.UserClientRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.c.RealmId, rl => rl.Id, (ac, rl) => new { ac, rl })
            .Join(db.Roles, acr => acr.ac.a.RoleId, ro => ro.Id, (acr, ro) => new { acr, ro })
            .Join(db.Tenants, acrro => acrro.acr.ac.c.TenantId, t => t.Id, (acrro, t) => new { acrro.acr, acrro.ro, t })
            .OrderBy(x => x.acr.ac.c.ClientId).ThenBy(x => x.ro.Name)
            .Select(x => new ClientAssignmentVm(x.ro.Id, x.acr.ac.c.Id, x.acr.ac.c.ClientId, x.acr.ac.c.ClientName, x.acr.rl.Id, x.acr.rl.Name, x.ro.Name, x.t.Name, x.acr.ac.a.IsActive))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddRealmAsync()
    {
        if (UserId == Guid.Empty || RealmAddRealmId == Guid.Empty || RealmAddRoleId == Guid.Empty)
            return await OnGetAsync();

        // Get user's tenant with tenant filtering
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return RedirectToPage("/Admin/Users/Index");
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        var user = await userQuery.FirstOrDefaultAsync();
        if (user is null) return RedirectToPage("/Admin/Users/Index");

        // Validate realm belongs to user's tenant
        var realmValid = await db.Realms.AsNoTracking()
            .AnyAsync(r => r.Id == RealmAddRealmId && r.TenantId == user.TenantId);
        if (!realmValid)
        {
            ModelState.AddModelError(string.Empty, "Realm does not belong to user's tenant.");
            return await OnGetAsync();
        }

        // Validate role belongs to user's tenant AND the selected realm
        var roleValid = await db.Roles.AsNoTracking()
            .AnyAsync(r => r.Id == RealmAddRoleId && r.TenantId == user.TenantId && r.RealmId == RealmAddRealmId);
        if (!roleValid)
        {
            ModelState.AddModelError(string.Empty, "Selected role does not belong to the selected realm or user's tenant.");
            return await OnGetAsync();
        }

        var exists = await db.UserRealmRoleAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.RealmId == RealmAddRealmId &&
            a.RoleId == RealmAddRoleId);

        if (!exists)
        {
            db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
            {
                UserId = UserId,
                RealmId = RealmAddRealmId,
                RoleId = RealmAddRoleId,
                IsActive = RealmIsActive
            });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteRealmAsync(Guid roleId, Guid realmId)
    {
        var entity = await db.UserRealmRoleAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.RealmId == realmId &&
            a.RoleId == roleId);

        if (entity is not null)
        {
            db.UserRealmRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostAddClientAsync()
    {
        if (UserId == Guid.Empty || ClientAddClientId == Guid.Empty || ClientAddRoleId == Guid.Empty)
            return await OnGetAsync();

        // Get user's tenant with tenant filtering
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return RedirectToPage("/Admin/Users/Index");
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        var user = await userQuery.FirstOrDefaultAsync();
        if (user is null) return RedirectToPage("/Admin/Users/Index");

        // Validate client belongs to user's tenant
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ClientAddClientId && c.TenantId == user.TenantId);
        if (client is null)
        {
            ModelState.AddModelError(string.Empty, "Client does not belong to user's tenant.");
            return await OnGetAsync();
        }

        // Validate role belongs to user's tenant
        var roleValid = await db.Roles.AsNoTracking()
            .AnyAsync(r => r.Id == ClientAddRoleId && r.TenantId == user.TenantId);
        if (!roleValid)
        {
            ModelState.AddModelError(string.Empty, "Role does not belong to user's tenant.");
            return await OnGetAsync();
        }

        var exists = await db.UserClientRoleAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.ClientId == ClientAddClientId &&
            a.RoleId == ClientAddRoleId);

        if (!exists)
        {
            db.UserClientRoleAssignments.Add(new UserClientRoleAssignment
            {
                UserId = UserId,
                ClientId = ClientAddClientId,
                RoleId = ClientAddRoleId,
                IsActive = ClientIsActive
            });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteClientAsync(Guid roleId, Guid clientId)
    {
        var entity = await db.UserClientRoleAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.ClientId == clientId &&
            a.RoleId == roleId);

        if (entity is not null)
        {
            db.UserClientRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }
}
