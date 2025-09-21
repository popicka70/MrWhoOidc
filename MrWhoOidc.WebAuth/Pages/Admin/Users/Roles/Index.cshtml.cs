using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Roles;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    [FromRoute]
    public Guid UserId { get; set; }

    [BindProperty]
    public Guid ClientId { get; set; }

    [BindProperty]
    public Guid RealmId { get; set; }

    [BindProperty]
    public Guid RoleId { get; set; }

    [BindProperty]
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<AssignmentVm> Assignments { get; private set; } = Array.Empty<AssignmentVm>();
    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();
    public IReadOnlyList<RoleVm> Roles { get; private set; } = Array.Empty<RoleVm>();

    public record AssignmentVm(Guid RoleId, Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, string RoleName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string RealmName);
    public record RoleVm(Guid Id, string Name, string RealmName);

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == UserId);
        if (!exists) return RedirectToPage("/Admin/Users/Index");

        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        Clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new ClientVm(c.Id, c.ClientId, r.Name))
            .OrderBy(c => c.ClientId).ToListAsync();
        Roles = await db.Roles.AsNoTracking()
            .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new RoleVm(r.Id, r.Name, rl.Name))
            .OrderBy(r => r.Name).ToListAsync();

        Assignments = await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.a.RealmId, r => r.Id, (ac, r) => new { ac, r })
            .Join(db.Roles, acr => acr.ac.a.RoleId, ro => ro.Id, (acr, ro) => new AssignmentVm(ro.Id, acr.ac.c.Id, acr.ac.c.ClientId, acr.ac.c.ClientName, acr.r.Id, acr.r.Name, ro.Name, acr.ac.a.IsActive))
            .OrderBy(a => a.ClientId).ThenBy(a => a.RoleName).ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (UserId == Guid.Empty || ClientId == Guid.Empty || RealmId == Guid.Empty || RoleId == Guid.Empty) return await OnGetAsync();
        var exists = await db.UserRoleAssignments.AnyAsync(a => a.UserId == UserId && a.ClientId == ClientId && a.RealmId == RealmId && a.RoleId == RoleId);
        if (!exists)
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = UserId, ClientId = ClientId, RealmId = RealmId, RoleId = RoleId, IsActive = IsActive });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid roleId, Guid clientId, Guid realmId)
    {
        var entity = await db.UserRoleAssignments.FirstOrDefaultAsync(a => a.UserId == UserId && a.ClientId == clientId && a.RealmId == realmId && a.RoleId == roleId);
        if (entity is not null)
        {
            db.UserRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }
}
