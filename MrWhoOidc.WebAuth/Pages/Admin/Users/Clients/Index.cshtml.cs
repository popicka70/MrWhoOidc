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
    public string UserHeading { get; private set; } = string.Empty;

    public record AssignmentVm(Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string RealmName);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null) return RedirectToPage("/Admin/Users/Index");
        UserHeading = BuildHeading(user.Username, user.Name);
    ViewData["UserHeading"] = UserHeading;

        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        // Avoid projecting to record type before OrderBy to keep query translatable by EF
        Clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
            .OrderBy(x => x.ClientId)
            .Select(x => new ClientVm(x.Id, x.ClientId, x.RealmName))
            .ToListAsync();

        // Same approach for assignments: order before projecting to record type
        Assignments = await db.UserClientAssignments.AsNoTracking().Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.a.RealmId, r => r.Id, (ac, r) => new { ac, r })
            .OrderBy(x => x.ac.c.ClientId)
            .Select(x => new AssignmentVm(x.ac.c.Id, x.ac.c.ClientId, x.ac.c.ClientName, x.r.Id, x.r.Name, x.ac.a.IsActive))
            .ToListAsync();

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

    private static string BuildHeading(string username, string? name)
        => string.IsNullOrWhiteSpace(name) || string.Equals(username, name, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username} ({name})";
}
