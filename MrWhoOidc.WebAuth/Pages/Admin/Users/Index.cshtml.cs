using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<User> Users { get; private set; } = Array.Empty<User>();

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public async Task OnGetAsync()
    {
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(u => u.Username.Contains(s) || (u.Email != null && u.Email.Contains(s)) || (u.Name != null && u.Name.Contains(s)));
        }
        Users = await q.OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var inUse = await db.Tokens.AnyAsync(t => t.UserId == id)
            || await db.Consents.AnyAsync(c => c.UserId == id)
            || await db.UserClientAssignments.AnyAsync(a => a.UserId == id)
            || await db.UserRoleAssignments.AnyAsync(a => a.UserId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete user; it is referenced by tokens, consents, or assignments.";
            return RedirectToPage();
        }
        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (entity is null) return RedirectToPage();
        db.Users.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }
}
