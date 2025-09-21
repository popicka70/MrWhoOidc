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

        // Order on raw fields, then project to record to keep EF translation
        Clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
            .OrderBy(x => x.ClientId)
            .Select(x => new ClientVm(x.Id, x.ClientId, x.RealmName))
            .ToListAsync();

        Roles = await db.Roles.AsNoTracking()
            .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new { r.Id, r.Name, RealmName = rl.Name })
            .OrderBy(x => x.Name)
            .Select(x => new RoleVm(x.Id, x.Name, x.RealmName))
            .ToListAsync();

        // Same approach for assignments: order before projecting to record type
        Assignments = await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.a.RealmId, r => r.Id, (ac, r) => new { ac, r })
            .Join(db.Roles, acr => acr.ac.a.RoleId, ro => ro.Id, (acr, ro) => new { acr, ro })
            .OrderBy(x => x.acr.ac.c.ClientId).ThenBy(x => x.ro.Name)
            .Select(x => new AssignmentVm(x.ro.Id, x.acr.ac.c.Id, x.acr.ac.c.ClientId, x.acr.ac.c.ClientName, x.acr.r.Id, x.acr.r.Name, x.ro.Name, x.acr.ac.a.IsActive))
            .ToListAsync();

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
