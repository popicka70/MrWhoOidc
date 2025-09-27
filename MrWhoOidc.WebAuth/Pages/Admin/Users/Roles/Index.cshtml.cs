using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Roles;

[Authorize]
public class IndexModel(AuthDbContext db) : UserPageModelBase
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

    public IReadOnlyList<RealmAssignmentVm> RealmAssignments { get; private set; } = Array.Empty<RealmAssignmentVm>();
    public IReadOnlyList<ClientAssignmentVm> ClientAssignments { get; private set; } = Array.Empty<ClientAssignmentVm>();

    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();
    public IReadOnlyList<RoleVm> Roles { get; private set; } = Array.Empty<RoleVm>();

    public record RealmAssignmentVm(Guid RoleId, Guid RealmId, string RealmName, string RoleName, bool IsActive);
    public record ClientAssignmentVm(Guid RoleId, Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, string RoleName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string RealmName);
    public record RoleVm(Guid Id, string Name, string RealmName, Guid RealmId);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null) return RedirectToPage("/Admin/Users/Index");
        SetHeading(user.Username, user.Name);

        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        // Clients with their realm name
        Clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
            .OrderBy(x => x.ClientId)
            .Select(x => new ClientVm(x.Id, x.ClientId, x.RealmName))
            .ToListAsync();

        // Roles with their realm name
        Roles = await db.Roles.AsNoTracking()
            .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new { RoleId = r.Id, r.Name, RealmName = rl.Name, RealmId = rl.Id })
            .OrderBy(x => x.Name)
            .Select(x => new RoleVm(x.RoleId, x.Name, x.RealmName, x.RealmId))
            .ToListAsync();

        // Realm-role assignments
        RealmAssignments = await db.UserRealmRoleAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms, ar => ar.a.RealmId, rl => rl.Id, (ar, rl) => new { ar, rl })
            .OrderBy(x => x.rl.Name).ThenBy(x => x.ar.r.Name)
            .Select(x => new RealmAssignmentVm(x.ar.r.Id, x.rl.Id, x.rl.Name, x.ar.r.Name, x.ar.a.IsActive))
            .ToListAsync();

        // Client-role assignments
        ClientAssignments = await db.UserClientRoleAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.c.RealmId, rl => rl.Id, (ac, rl) => new { ac, rl })
            .Join(db.Roles, acr => acr.ac.a.RoleId, ro => ro.Id, (acr, ro) => new { acr, ro })
            .OrderBy(x => x.acr.ac.c.ClientId).ThenBy(x => x.ro.Name)
            .Select(x => new ClientAssignmentVm(x.ro.Id, x.acr.ac.c.Id, x.acr.ac.c.ClientId, x.acr.ac.c.ClientName, x.acr.rl.Id, x.acr.rl.Name, x.ro.Name, x.acr.ac.a.IsActive))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddRealmAsync()
    {
        if (UserId == Guid.Empty || RealmAddRealmId == Guid.Empty || RealmAddRoleId == Guid.Empty)
            return await OnGetAsync();

        // Validate role belongs to the selected realm
        var valid = await db.Roles.AsNoTracking().AnyAsync(r => r.Id == RealmAddRoleId && r.RealmId == RealmAddRealmId);
        if (!valid)
        {
            ModelState.AddModelError(string.Empty, "Selected role does not belong to the selected realm.");
            return await OnGetAsync();
        }

        var exists = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == UserId && a.RealmId == RealmAddRealmId && a.RoleId == RealmAddRoleId);
        if (!exists)
        {
            db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = UserId, RealmId = RealmAddRealmId, RoleId = RealmAddRoleId, IsActive = RealmIsActive });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteRealmAsync(Guid roleId, Guid realmId)
    {
        var entity = await db.UserRealmRoleAssignments.FirstOrDefaultAsync(a => a.UserId == UserId && a.RealmId == realmId && a.RoleId == roleId);
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

        var exists = await db.UserClientRoleAssignments.AnyAsync(a => a.UserId == UserId && a.ClientId == ClientAddClientId && a.RoleId == ClientAddRoleId);
        if (!exists)
        {
            db.UserClientRoleAssignments.Add(new UserClientRoleAssignment { UserId = UserId, ClientId = ClientAddClientId, RoleId = ClientAddRoleId, IsActive = ClientIsActive });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteClientAsync(Guid roleId, Guid clientId)
    {
        var entity = await db.UserClientRoleAssignments.FirstOrDefaultAsync(a => a.UserId == UserId && a.ClientId == clientId && a.RoleId == roleId);
        if (entity is not null)
        {
            db.UserClientRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

}
