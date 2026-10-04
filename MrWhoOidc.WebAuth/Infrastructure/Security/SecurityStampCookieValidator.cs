using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// H5: Validates the SecurityStamp claim on the main auth cookie
/// (<c>__Host-mrwhooidc-auth</c>, scheme = CookieAuthenticationDefaults.AuthenticationScheme)
/// against the user's current SecurityStamp in the database.
/// The claim is added at sign-in by every sign-in path whenever the account has a stamp. When present, a
/// mismatch (password/email/MFA change rotated the stamp) or a deleted account rejects the principal and
/// signs the session out. When ABSENT, the session is kept only while the account still has no stamp:
/// once a stamp exists (set at account creation, or by the first password/MFA change), a stamp-less
/// cookie predates it and is rejected. Otherwise such sessions survived a password reset.
/// Transient infrastructure errors are logged and never cause sign-out.
/// </summary>
public static class SecurityStampCookieValidator
{
    public const string SecurityStampClaimType = "mrwho:sec_stamp";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var stampClaim = context.Principal?.FindFirst(SecurityStampClaimType);
        if (stampClaim is null || string.IsNullOrWhiteSpace(stampClaim.Value))
        {
            await ValidateStamplessAsync(context).ConfigureAwait(false);
            return;
        }

        // NameIdentifier is the per-tenant User.Id, which equals UserAccount.Id only for the home tenant;
        // in any other tenant the explicit account-id claim is the only correct key.
        var userIdClaim = context.Principal?.FindFirst(UserClaimTypes.UserAccountId)
                          ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return; // Lenient: cannot resolve the user id, do not invalidate.
        }

        try
        {
            var db = context.HttpContext.RequestServices.GetService<AuthDbContext>();
            if (db is null)
            {
                return;
            }

            var account = await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == userId)
                .ConfigureAwait(false);

            if (account is null || string.IsNullOrWhiteSpace(account.SecurityStamp))
            {
                // User no longer exists (or the stamp was cleared): reject the session.
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            var currentBytes = Encoding.UTF8.GetBytes(account.SecurityStamp);
            var cookieBytes = Encoding.UTF8.GetBytes(stampClaim.Value);
            if (!CryptographicOperations.FixedTimeEquals(currentBytes, cookieBytes))
            {
                // Stamp was rotated (password/email/MFA change) => stale cookie.
                await RejectAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never sign a user out because of a transient DB/infrastructure error.
            var loggerFactory = context.HttpContext.RequestServices.GetService<ILoggerFactory>();
            loggerFactory?.CreateLogger("SecurityStampCookieValidator")
                .LogWarning(ex, "SecurityStamp validation failed; session left intact");
        }
    }

    private static async Task ValidateStamplessAsync(CookieValidatePrincipalContext context)
    {
        try
        {
            var db = context.HttpContext.RequestServices.GetService<AuthDbContext>();
            if (db is null)
            {
                return;
            }

            Guid? accountId = null;
            if (Guid.TryParse(context.Principal?.FindFirst(UserClaimTypes.UserAccountId)?.Value, out var claimedAccountId))
            {
                accountId = claimedAccountId;
            }
            else if (Guid.TryParse(context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            {
                accountId = await db.Users.AsNoTracking().IgnoreQueryFilters()
                    .Where(u => u.Id == userId)
                    .Select(u => u.UserAccountId ?? u.Id)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }

            if (accountId is not { } id || id == Guid.Empty)
            {
                return; // Cannot resolve the account: leave the session to the other checks.
            }

            var stamp = await db.UserAccounts.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => a.SecurityStamp)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stamp))
            {
                // The account has a stamp this cookie never carried: it was issued before a credential change.
                await RejectAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var loggerFactory = context.HttpContext.RequestServices.GetService<ILoggerFactory>();
            loggerFactory?.CreateLogger("SecurityStampCookieValidator")
                .LogWarning(ex, "SecurityStamp validation failed; session left intact");
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
