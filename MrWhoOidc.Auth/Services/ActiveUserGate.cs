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
    /// user's global account (so the auth cookies that carry no session fail <c>SecurityStampCookieValidator</c>
    /// on their next request), revokes the user's live tokens, and revokes the server-side OP sessions (F1) of
    /// the user in this tenant. Changes are saved by the caller.
    /// </summary>
    /// <param name="db">The context of the change.</param>
    /// <param name="user">The per-tenant user to deactivate.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="opSessions">The session store; the sessions are revoked when it is available.</param>
    /// <returns>The number of tokens revoked.</returns>
    public static async Task<int> DeactivateAsync(
        AuthDbContext db,
        User user,
        CancellationToken ct = default,
        IOpSessionService? opSessions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
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

        // F1: the server-side sessions of the user in this tenant end as well.
        if (opSessions is not null)
        {
            await opSessions.RevokeAllForUserAsync(
                user.TenantId,
                userId,
                exceptSessionId: null,
                reason: "the user was deactivated",
                ct: ct).ConfigureAwait(false);
        }

        return tokens.Count;
    }
}
