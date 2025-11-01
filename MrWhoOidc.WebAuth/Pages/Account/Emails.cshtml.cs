using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class EmailsModel(AuthDbContext db, IEmailConfirmationWorkflow emailWorkflow, ILogger<EmailsModel> logger) : PageModel
{
    private readonly AuthDbContext _db = db;
    private readonly IEmailConfirmationWorkflow _emailWorkflow = emailWorkflow;
    private readonly ILogger<EmailsModel> _logger = logger;

    public List<AlternativeEmailViewModel> AlternativeEmails { get; private set; } = new();

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? PrimaryEmail { get; private set; }
    public bool PrimaryEmailVerified { get; private set; }
    public DateTimeOffset? PrimaryEmailVerifiedAt { get; private set; }

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
        PrimaryEmailVerified = user.EmailVerified;
        PrimaryEmailVerifiedAt = user.EmailVerifiedAt;

        // Get all alternative emails for the user
        var emails = await _db.UserAlternativeEmails
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
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/emails") });

        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }

        var normalizedEmail = NewEmail.ToUpperInvariant();

        // Check if email already exists (primary or alternative)
        var emailExists = user.NormalizedEmail == normalizedEmail ||
            await _db.UserAlternativeEmails.AnyAsync(e => e.UserId == user.Id && e.NormalizedEmail == normalizedEmail);

        if (emailExists)
        {
            ErrorMessage = "This email address is already associated with your account.";
            await OnGetAsync();
            return Page();
        }

        // Check if email is used by another user (tenant-scoped uniqueness)
        var emailUsedByOther = await _db.Users.AnyAsync(u => u.TenantId == user.TenantId && u.NormalizedEmail == normalizedEmail);
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

        _db.UserAlternativeEmails.Add(alternativeEmail);
        await _db.SaveChangesAsync();

        try
        {
            await _emailWorkflow.SendAlternativeAsync(user, alternativeEmail, HttpContext.RequestAborted);
            Message = $"Alternative email {NewEmail} added successfully. We've sent a verification link.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Alternative email {NewEmail} added, but we couldn't send the verification email. Please try resending.";
            _logger.LogWarning(ex, "Failed to send alternative email verification for user {UserId}", user.Id);
        }

        NewEmail = string.Empty;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResendPrimaryAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return RedirectToPage("/login", new { returnUrl = Url.Page("/account/emails") });
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            ErrorMessage = "No primary email address is configured.";
            return RedirectToPage();
        }

        if (user.EmailVerified)
        {
            Message = $"Primary email {user.Email} is already verified.";
            return RedirectToPage();
        }

        try
        {
            await _emailWorkflow.SendPrimaryAsync(user, HttpContext.RequestAborted);
            Message = $"Verification email sent to {user.Email}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = "We couldn't send the verification email. Please try again.";
            _logger.LogWarning(ex, "Failed to resend primary email verification for user {UserId}", user.Id);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/emails") });

        var email = await _db.UserAlternativeEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == user.Id);

        if (email is null)
        {
            ErrorMessage = "Email address not found.";
            return RedirectToPage();
        }

        _db.UserAlternativeEmails.Remove(email);
        await _db.SaveChangesAsync();

        Message = $"Email address {email.Email} removed successfully.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResendAsync(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/emails") });

        var email = await _db.UserAlternativeEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == user.Id);

        if (email is null)
        {
            ErrorMessage = "Email address not found.";
            return RedirectToPage();
        }

        if (email.IsVerified)
        {
            Message = $"Email address {email.Email} is already verified.";
            return RedirectToPage();
        }

        try
        {
            await _emailWorkflow.SendAlternativeAsync(user, email, HttpContext.RequestAborted);
            Message = $"Verification email resent to {email.Email}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"We couldn't resend the verification email to {email.Email}. Please try again.";
            _logger.LogWarning(ex, "Failed to resend alternative email verification for user {UserId}", user.Id);
        }

        return RedirectToPage();
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await _db.Users
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
