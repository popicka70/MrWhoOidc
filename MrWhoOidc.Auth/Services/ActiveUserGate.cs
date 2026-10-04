using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// The one check every sign-in path makes before it hands out a session or a code for a per-tenant
/// <see cref="User"/>: the user must exist and must not be deactivated. Deactivation itself also ends the
/// sessions and tokens the user already holds (<see cref="DeactivateAsync"/>).
/// </summary>
public static class ActiveUserGate
{
    public static bool IsActive(User? user) => user is { Status: UserStatus.Active };

    /// <summary>True when the per-tenant user exists and is active. A missing user is not active.</summary>
    public static Task<bool> IsActiveAsync(AuthDbContext db, Guid userId, CancellationToken ct = default)
        => db.Users.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);

    /// <summary>
    /// True only when a per-tenant user with this id exists and is deactivated. Used where the id may also be a
    /// global account id (platform sessions), so a missing row must not count as deactivated.
    /// </summary>
    public static Task<bool> IsDeactivatedAsync(AuthDbContext db, Guid userId, CancellationToken ct = default)
        => db.Users.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(u => u.Id == userId && u.Status == UserStatus.Deactivated, ct);

    /// <summary>
    /// Deactivates a tracked per-tenant user and ends what it already holds: rotates the SecurityStamp of the
    /// user's global account (so auth cookies fail <c>SecurityStampCookieValidator</c> on their next request)
    /// and revokes the user's live tokens. Changes are saved by the caller.
    /// </summary>
    /// <returns>The number of tokens revoked.</returns>
    public static async Task<int> DeactivateAsync(AuthDbContext db, User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = DateTimeOffset.UtcNow;
        user.Status = UserStatus.Deactivated;
        user.DeactivatedAt = now;

        // Same resolution as IUserAccountService.FindForUserAsync: the explicit link, else the legacy home-user id.
        var accountId = user.UserAccountId ?? user.Id;
        var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        if (account is not null)
        {
            account.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        var userId = user.Id;
        var tokens = await db.Tokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        return tokens.Count;
    }
}
