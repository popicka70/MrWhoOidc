using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Users;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel(AuthDbContext db, ILogger<IndexModel> logger) : PageModel
{
    public sealed record UnassignedUserRow(
        Guid Id,
        string Username,
        string? Email,
        string? Name,
        DateTimeOffset CreatedAt,
        bool EmailVerified,
        bool TotpEnabled,
        DateTimeOffset? LockedOutUntil,
        int MembershipCount);

    public IReadOnlyList<UnassignedUserRow> Users { get; private set; } = [];
    public int TotalCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.UserAccounts.AsNoTracking()
            .Where(account => !db.UserTenantMemberships.Any(membership =>
                membership.UserAccountId == account.Id
                && membership.Status == TenantMembershipStatus.Active
                && (membership.ExpiresAt == null || membership.ExpiresAt > now)));

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(account =>
                account.Username.Contains(term)
                || (account.Email != null && account.Email.Contains(term))
                || (account.Name != null && account.Name.Contains(term)));
        }

        TotalCount = await query.CountAsync(ct);
        Users = await query
            .OrderBy(account => account.Username)
            .Take(500)
            .Select(account => new UnassignedUserRow(
                account.Id,
                account.Username,
                account.Email,
                account.Name,
                account.CreatedAt,
                account.EmailVerified,
                account.TotpEnabled,
                account.LockedOutUntil,
                db.UserTenantMemberships.Count(membership => membership.UserAccountId == account.Id)))
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostTerminateAsync(Guid id, CancellationToken ct)
    {
        if (GetCurrentUserAccountId() == id)
        {
            TempData["ErrorMessage"] = "You cannot terminate the currently authenticated account.";
            return RedirectToPage(new { Search });
        }

        var account = await db.UserAccounts.FirstOrDefaultAsync(userAccount => userAccount.Id == id, ct);
        if (account is null)
        {
            TempData["ErrorMessage"] = "User account was not found.";
            return RedirectToPage(new { Search });
        }

        var now = DateTimeOffset.UtcNow;
        var hasActiveMembership = await db.UserTenantMemberships.AnyAsync(membership =>
            membership.UserAccountId == id
            && membership.Status == TenantMembershipStatus.Active
            && (membership.ExpiresAt == null || membership.ExpiresAt > now), ct);
        if (hasActiveMembership)
        {
            TempData["ErrorMessage"] = "Only accounts with no active tenant memberships can be terminated here.";
            return RedirectToPage(new { Search });
        }

        var resetTokens = await db.PasswordResetTokens
            .Where(token => token.UserAccountId == id)
            .ToListAsync(ct);
        db.PasswordResetTokens.RemoveRange(resetTokens);
        db.UserAccounts.Remove(account);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Platform admin {AdminUser} terminated unassigned UserAccount {AccountId}",
            User.Identity?.Name ?? "unknown",
            id);

        TempData["SuccessMessage"] = $"User account '{account.Username}' was terminated.";
        return RedirectToPage(new { Search });
    }

    private Guid? GetCurrentUserAccountId()
    {
        var value = User.FindFirstValue(UserClaimTypes.UserAccountId)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}