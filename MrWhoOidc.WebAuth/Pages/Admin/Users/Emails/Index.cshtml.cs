using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Emails;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    [FromRoute]
    public Guid UserId { get; set; }

    public IReadOnlyList<UserAlternativeEmail> Items { get; private set; } = Array.Empty<UserAlternativeEmail>();

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == UserId);
        if (!exists) return RedirectToPage("/Admin/Users/Index");
        Items = await db.UserAlternativeEmails.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .OrderByDescending(a => a.IsVerified).ThenBy(a => a.Email)
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return await OnGetAsync();
        email = email.Trim().ToLowerInvariant();
        var dup = await db.UserAlternativeEmails.AnyAsync(a => a.UserId == UserId && a.Email == email);
        if (!dup)
        {
            db.UserAlternativeEmails.Add(new UserAlternativeEmail { UserId = UserId, Email = email, IsVerified = false });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid emailId)
    {
        var entity = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == emailId && a.UserId == UserId);
        if (entity is not null)
        {
            var newValue = !entity.IsVerified;
            entity.IsVerified = newValue;
            entity.VerifiedAt = newValue ? (entity.VerifiedAt ?? DateTimeOffset.UtcNow) : null;
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid emailId)
    {
        var entity = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == emailId && a.UserId == UserId);
        if (entity is not null)
        {
            db.UserAlternativeEmails.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }
}
