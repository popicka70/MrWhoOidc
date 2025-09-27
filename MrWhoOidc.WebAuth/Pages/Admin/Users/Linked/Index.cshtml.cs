using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Linked;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    [FromRoute]
    public Guid UserId { get; set; }

    public IReadOnlyList<ExternalIdentity> Items { get; private set; } = Array.Empty<ExternalIdentity>();
    public string UserHeading { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null) return RedirectToPage("/Admin/Users/Index");
        UserHeading = BuildHeading(user.Username, user.Name);
        ViewData["UserHeading"] = UserHeading;
        Items = await db.ExternalIdentities.AsNoTracking()
            .Where(e => e.UserId == UserId)
            .OrderBy(e => e.ProviderName).ThenBy(e => e.Issuer).ThenBy(e => e.Subject)
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid linkId)
    {
        var entity = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.Id == linkId && e.UserId == UserId);
        if (entity is not null)
        {
            db.ExternalIdentities.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    private static string BuildHeading(string username, string? name)
        => string.IsNullOrWhiteSpace(name) || string.Equals(username, name, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username} ({name})";
}
