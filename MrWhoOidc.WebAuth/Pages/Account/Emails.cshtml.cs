using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class EmailsModel(AuthDbContext db) : PageModel
{
    public List<AlternativeEmailViewModel> AlternativeEmails { get; private set; } = new();
    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? PrimaryEmail { get; private set; }

    [BindProperty]
    [Required(ErrorMessage = "Email address is required")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address")]
    [MaxLength(256, ErrorMessage = "Email address cannot exceed 256 characters")]
    public string NewEmail { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return;

        PrimaryEmail = user.Email;

        // Get all alternative emails for the user
        var emails = await db.UserAlternativeEmails
            .AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .OrderByDescending(e => e.IsVerified)
            .ThenBy(e => e.Email)
            .ToListAsync();

        AlternativeEmails = emails.Select(e => new AlternativeEmailViewModel
        {
            Id = e.Id,
            Email = e.Email,
            IsVerified = e.IsVerified,
            VerifiedAt = e.VerifiedAt
        }).ToList();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/Emails") });

        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }

        var normalizedEmail = NewEmail.ToUpperInvariant();

        // Check if email already exists (primary or alternative)
        var emailExists = user.NormalizedEmail == normalizedEmail ||
                         await db.UserAlternativeEmails.AnyAsync(e => e.UserId == user.Id && e.NormalizedEmail == normalizedEmail);

        if (emailExists)
        {
            ErrorMessage = "This email address is already associated with your account.";
            await OnGetAsync();
            return Page();
        }

        // Check if email is used by another user (tenant-scoped uniqueness)
        var emailUsedByOther = await db.Users.AnyAsync(u => u.TenantId == user.TenantId && u.NormalizedEmail == normalizedEmail);
        if (emailUsedByOther)
        {
            ErrorMessage = "This email address is already in use by another account.";
            await OnGetAsync();
            return Page();
        }

        var alternativeEmail = new UserAlternativeEmail
        {
            UserId = user.Id,
            Email = NewEmail,
            NormalizedEmail = normalizedEmail,
            IsVerified = false,
            VerifiedAt = null
        };

        db.UserAlternativeEmails.Add(alternativeEmail);
        await db.SaveChangesAsync();

        Message = $"Alternative email {NewEmail} added successfully. A verification email will be sent shortly.";
        NewEmail = string.Empty;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/Emails") });

        var email = await db.UserAlternativeEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == user.Id);

        if (email is null)
        {
            ErrorMessage = "Email address not found.";
            return RedirectToPage();
        }

        db.UserAlternativeEmails.Remove(email);
        await db.SaveChangesAsync();

        Message = $"Email address {email.Email} removed successfully.";

        return RedirectToPage();
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId);
    }
}

public class AlternativeEmailViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}
