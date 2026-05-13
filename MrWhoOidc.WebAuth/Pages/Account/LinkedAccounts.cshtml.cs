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
    public List<IdentityProviderViewModel> AvailableProviders { get; private set; } = new();
    public string? Message { get; private set; }

    public async Task OnGetAsync()
    {
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

        AvailableProviders = await db.IdentityProviders
            .AsNoTracking()
            .Where(p => p.Enabled)
            .Select(p => new IdentityProviderViewModel
            {
                Name = p.Name,
                DisplayName = p.Name // Assuming DisplayName is not in the model or we just use Name
            })
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostUnlinkAsync(Guid accountId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/LinkedAccounts") });

        var externalIdentity = await db.ExternalIdentities
            .FirstOrDefaultAsync(ei => ei.Id == accountId && ei.UserId == user.Id);

        if (externalIdentity is null)
        {
            Message = "Linked account not found.";
            return RedirectToPage();
        }

        // Safety check: ensure user has a password (on global UserAccount) or other external identities
        var hasPassword = await HasGlobalPasswordAsync(user);
        var otherIdentitiesCount = await db.ExternalIdentities
            .CountAsync(ei => ei.UserId == user.Id && ei.Id != accountId);

        if (!hasPassword && otherIdentitiesCount == 0)
        {
            Message = "Cannot unlink this account. You must have a password or at least one other linked account to maintain access to your account.";
            return RedirectToPage();
        }

        db.ExternalIdentities.Remove(externalIdentity);
        await db.SaveChangesAsync();

        Message = $"Successfully unlinked {externalIdentity.ProviderName ?? "external account"}.";

        return RedirectToPage();
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

public class IdentityProviderViewModel
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
