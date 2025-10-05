using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Emails;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(AuthDbContext db) : UserPageModelBase
{
    [FromRoute]
    public Guid UserId { get; set; }

    public IReadOnlyList<UserAlternativeEmail> Items { get; private set; } = Array.Empty<UserAlternativeEmail>();

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null) return RedirectToPage("/Admin/Users/Index");
        SetHeading(user.Username, user.Name);
        Items = await db.UserAlternativeEmails.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .OrderByDescending(a => a.IsVerified).ThenBy(a => a.Email)
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToPage(new { userId = UserId });
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null)
        {
            return RedirectToPage("/Admin/Users/Index");
        }

        try
        {
            var formatted = EmailNormalizer.FormatForStorage(email, required: true, out var normalized)
                ?? throw new ValidationException("Email is required.");
            var normalizedValue = normalized ?? string.Empty;

            if (string.Equals(user.NormalizedEmail, normalizedValue, StringComparison.Ordinal))
            {
                return RedirectToPage(new { userId = UserId });
            }

            var duplicate = await db.UserAlternativeEmails.AnyAsync(a => a.NormalizedEmail == normalizedValue);
            if (!duplicate)
            {
                db.UserAlternativeEmails.Add(new UserAlternativeEmail { UserId = UserId, Email = formatted, IsVerified = false });
                await db.SaveChangesAsync();
            }
        }
        catch (ValidationException)
        {
            // Invalid email format; ignore and reload page without changes.
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
