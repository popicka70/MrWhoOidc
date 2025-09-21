using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Clients;

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
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<AssignmentVm> Assignments { get; private set; } = Array.Empty<AssignmentVm>();
    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    public record AssignmentVm(Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string RealmName);

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == UserId);
        if (!exists) return RedirectToPage("/Admin/Users/Index");

        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        Clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new ClientVm(c.Id, c.ClientId, r.Name))
            .OrderBy(c => c.ClientId).ToListAsync();

        Assignments = await db.UserClientAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.a.RealmId, r => r.Id, (ac, r) => new AssignmentVm(ac.c.Id, ac.c.ClientId, ac.c.ClientName, r.Id, r.Name, ac.a.IsActive))
            .OrderBy(a => a.ClientId).ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (UserId == Guid.Empty || ClientId == Guid.Empty || RealmId == Guid.Empty) return await OnGetAsync();
        var exists = await db.UserClientAssignments.AnyAsync(a => a.UserId == UserId && a.ClientId == ClientId && a.RealmId == RealmId);
        if (!exists)
        {
            db.UserClientAssignments.Add(new UserClientAssignment { UserId = UserId, ClientId = ClientId, RealmId = RealmId, IsActive = IsActive });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid clientId, Guid realmId)
    {
        var entity = await db.UserClientAssignments.FirstOrDefaultAsync(a => a.UserId == UserId && a.ClientId == clientId && a.RealmId == realmId);
        if (entity is not null)
        {
            db.UserClientAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }
}
