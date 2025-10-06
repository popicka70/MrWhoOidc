using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public string Username { get; private set; } = string.Empty;
    public string? Name { get; private set; }
    public string? Email { get; private set; }
    public bool EmailVerified { get; private set; }
    public bool MfaEnabled { get; private set; }
    public int ActiveSessionsCount { get; private set; }
    public int ActiveConsentsCount { get; private set; }
    public int LinkedAccountsCount { get; private set; }
    public int AlternativeEmailsCount { get; private set; }
    public DateTimeOffset AccountCreatedAt { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return;

        Username = user.Username;
        Name = user.Name;
        Email = user.Email;
        EmailVerified = user.EmailVerified;
        MfaEnabled = user.TotpEnabled;
        AccountCreatedAt = user.CreatedAt;

        // Count active sessions (tokens)
        ActiveSessionsCount = await db.Tokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow)
            .CountAsync();

        // Count active consents
        ActiveConsentsCount = await db.Consents
            .Where(c => c.UserId == user.Id && c.RevokedAt == null)
            .CountAsync();

        // Count linked external accounts
        LinkedAccountsCount = await db.ExternalIdentities
            .Where(e => e.UserId == user.Id)
            .CountAsync();

        // Count alternative emails
        AlternativeEmailsCount = await db.UserAlternativeEmails
            .Where(e => e.UserId == user.Id)
            .CountAsync();
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
    }
}
