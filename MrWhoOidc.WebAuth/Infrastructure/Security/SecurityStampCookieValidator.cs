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

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// H5: Validates the SecurityStamp claim on the main auth cookie
/// (<c>__Host-mrwhooidc-auth</c>, scheme = CookieAuthenticationDefaults.AuthenticationScheme)
/// against the user's current SecurityStamp in the database.
/// The claim is added at sign-in by the login flow. When the claim is ABSENT the
/// validator is lenient (legacy sessions are not invalidated). When present, a
/// mismatch (password/email/MFA change rotated the stamp) or a deleted account
/// rejects the principal and signs the session out.
/// Transient infrastructure errors are logged and never cause sign-out.
/// </summary>
public static class SecurityStampCookieValidator
{
    public const string SecurityStampClaimType = "mrwho:sec_stamp";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // Lenient: sessions minted before the stamp claim existed are left untouched.
        var stampClaim = context.Principal?.FindFirst(SecurityStampClaimType);
        if (stampClaim is null || string.IsNullOrWhiteSpace(stampClaim.Value))
        {
            return;
        }

        // The NameIdentifier claim carries the shared User.Id / UserAccount.Id.
        var userIdClaim = context.Principal?.FindFirst(ClaimTypes.NameIdentifier);
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

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
