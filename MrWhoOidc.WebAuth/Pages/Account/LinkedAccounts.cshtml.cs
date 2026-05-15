using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class LinkedAccountsModel(AuthDbContext db, IUserAccountService userAccountService) : PageModel
{
    public List<LinkedAccountViewModel> LinkedAccounts { get; private set; } = new();
    public string? Message { get; private set; }

    public async Task OnGetAsync(string? message = null)
    {
        Message = message;

        var user = await GetCurrentUserAsync();
        if (user is null) return;

        // Get all external identities for the user
        var externalIdentities = await db.ExternalIdentities
            .AsNoTracking()
            .Where(ei => ei.UserId == user.Id)
            .OrderByDescending(ei => ei.LastSeenAt)
            .ToListAsync();

        LinkedAccounts = externalIdentities.Select(ei => new LinkedAccountViewModel
        {
            Id = ei.Id,
            Issuer = ei.Issuer,
            Subject = ei.Subject,
            ProviderName = ei.ProviderName ?? "Unknown Provider",
            LinkedAt = ei.CreatedAt,
            LastSeenAt = ei.LastSeenAt
        }).ToList();
    }

    public async Task<IActionResult> OnGetLinkAccountAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/LinkedAccounts") });

        var returnUrl = Url.Page("/Account/LinkedAccounts", new { area = "" });
        if (returnUrl == null)
        {
            returnUrl = "/";
        }

        return RedirectToPage("/Auth/Providers/Select", new { link = true, returnUrl });
    }

    public async Task<IActionResult> OnPostUnlinkAsync(Guid accountId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/LinkedAccounts") });

        var externalIdentity = await db.ExternalIdentities
            .FirstOrDefaultAsync(ei => ei.Id == accountId && ei.UserId == user.Id);

        if (externalIdentity is null)
        {
            return RedirectToPage(new { message = "Linked account not found." });
        }

        // Safety check: ensure user has a password (on global UserAccount) or other external identities
        var hasPassword = await HasGlobalPasswordAsync(user);
        var otherIdentitiesCount = await db.ExternalIdentities
            .CountAsync(ei => ei.UserId == user.Id && ei.Id != accountId);

        if (!hasPassword && otherIdentitiesCount == 0)
        {
            return RedirectToPage(new
            {
                message = "Cannot unlink this account. You must have a password or at least one other linked account to maintain access to your account."
            });
        }

        db.ExternalIdentities.Remove(externalIdentity);
        await db.SaveChangesAsync();

        return RedirectToPage(new
        {
            message = $"Successfully unlinked {externalIdentity.ProviderName ?? "external account"}."
        });
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    private async Task<bool> HasGlobalPasswordAsync(User user)
    {
        // Check global UserAccount for password
        UserAccount? account = null;
        if (!string.IsNullOrEmpty(user.Email))
        {
            account = await userAccountService.FindByEmailAsync(user.Email);
        }
        if (account is null)
        {
            account = await userAccountService.FindByUsernameAsync(user.Username);
        }
        return account is not null && !string.IsNullOrWhiteSpace(account.PasswordHash);
    }
}

public class LinkedAccountViewModel
{
    public Guid Id { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
