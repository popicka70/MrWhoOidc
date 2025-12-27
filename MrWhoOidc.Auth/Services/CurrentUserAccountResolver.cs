using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for resolving the current user account from a claims principal.
/// </summary>
public interface ICurrentUserAccountResolver
{
    /// <summary>
    /// Resolves the user account from the provided principal.
    /// </summary>
    /// <param name="principal">The claims principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolution result containing the user and account if found.</returns>
    Task<UserAccountResolution?> ResolveAsync(ClaimsPrincipal? principal, CancellationToken ct = default);
}

/// <summary>
/// Maps the authenticated principal back to the decoupled UserAccount/User entry using claim data or email fallbacks.
/// </summary>
internal sealed class CurrentUserAccountResolver(AuthDbContext dbContext, ILogger<CurrentUserAccountResolver> logger)
    : ICurrentUserAccountResolver
{
    public async Task<UserAccountResolution?> ResolveAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (!TryGetGuid(principal, ClaimTypes.NameIdentifier, out var userId) &&
            !TryGetGuid(principal, "sub", out userId))
        {
            logger.LogWarning("Authenticated principal missing subject identifier claims.");
            return null;
        }

        var userSnapshot = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserSnapshot(u.Id, u.NormalizedEmail, u.Email))
            .FirstOrDefaultAsync(ct);

        if (userSnapshot is null)
        {
            logger.LogWarning("Authenticated principal mapped to user id {UserId}, but no user row exists.", userId);
            return null;
        }

        var normalizedEmail = NormalizeEmail(principal)
                              ?? EmailNormalizer.NormalizeForLookup(userSnapshot.NormalizedEmail ?? userSnapshot.Email);

        var (accountSnapshot, confidence) = await ResolveAccountSnapshotAsync(principal, userSnapshot.Id, ct);

        if (accountSnapshot is null && !string.IsNullOrEmpty(normalizedEmail))
        {
            accountSnapshot = await dbContext.UserAccounts.AsNoTracking()
                .Where(a => a.NormalizedEmail == normalizedEmail)
                .Select(a => new UserAccountSnapshot(a.Id, a.NormalizedEmail, a.Email))
                .FirstOrDefaultAsync(ct);

            if (accountSnapshot is not null)
            {
                confidence = UserIdentityConfidence.EmailLookup;
            }
        }

        if (accountSnapshot is null)
        {
            accountSnapshot = await dbContext.UserAccounts.AsNoTracking()
                .Where(a => a.Id == userSnapshot.Id)
                .Select(a => new UserAccountSnapshot(a.Id, a.NormalizedEmail, a.Email))
                .FirstOrDefaultAsync(ct);
        }

        if (accountSnapshot is not null)
        {
            normalizedEmail ??= EmailNormalizer.NormalizeForLookup(accountSnapshot.NormalizedEmail ?? accountSnapshot.Email);
        }

        return new UserAccountResolution(
            userSnapshot.Id,
            accountSnapshot?.Id ?? userSnapshot.Id,
            normalizedEmail,
            confidence);
    }

    private async Task<(UserAccountSnapshot? Snapshot, UserIdentityConfidence Confidence)> ResolveAccountSnapshotAsync(
        ClaimsPrincipal principal,
        Guid fallbackUserId,
        CancellationToken ct)
    {
        if (TryGetGuid(principal, UserClaimTypes.UserAccountId, out var accountIdFromClaim))
        {
            var snapshot = await dbContext.UserAccounts.AsNoTracking()
                .Where(a => a.Id == accountIdFromClaim)
                .Select(a => new UserAccountSnapshot(a.Id, a.NormalizedEmail, a.Email))
                .FirstOrDefaultAsync(ct);

            if (snapshot is null)
            {
                logger.LogWarning("Principal contained user-account claim {AccountId} that does not exist.", accountIdFromClaim);
            }
            else
            {
                return (snapshot, UserIdentityConfidence.ExplicitClaim);
            }
        }

        var legacySnapshot = await dbContext.UserAccounts.AsNoTracking()
            .Where(a => a.Id == fallbackUserId)
            .Select(a => new UserAccountSnapshot(a.Id, a.NormalizedEmail, a.Email))
            .FirstOrDefaultAsync(ct);

        return (legacySnapshot, UserIdentityConfidence.LegacyClaim);
    }

    private static bool TryGetGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        var raw = principal.FindFirst(claimType)?.Value;
        return Guid.TryParse(raw, out value);
    }

    private static string? NormalizeEmail(ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email");

        return EmailNormalizer.NormalizeForLookup(email);
    }
}

internal sealed record UserAccountSnapshot(Guid Id, string? NormalizedEmail, string? Email);

internal sealed record UserSnapshot(Guid Id, string? NormalizedEmail, string? Email);

public readonly record struct UserAccountResolution(Guid UserId, Guid? UserAccountId, string? NormalizedEmail, UserIdentityConfidence Confidence);

public enum UserIdentityConfidence
{
    ExplicitClaim = 0,
    LegacyClaim = 1,
    EmailLookup = 2
}
