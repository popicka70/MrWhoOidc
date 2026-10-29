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
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// Validates the main auth cookie (<c>__Host-mrwhooidc-auth</c>, scheme =
/// CookieAuthenticationDefaults.AuthenticationScheme) against the account it belongs to.
/// <para>
/// The security stamp answered two questions: "were the credentials changed after the cookie was issued?" and
/// "does the account still exist?". The first one is now answered by the server-side OP session (F1): a
/// credential change revokes the sessions of the account next to rotating the stamp, and the session check
/// rejects the cookie. What is left here is what a session cannot know: the account must still exist, and the
/// per-tenant user must not be deactivated.
/// </para>
/// <para>
/// The stamp comparison stays for the cookies that carry no session (a cookie that was issued before the
/// session store, or by a version without it): that is the compatibility path, and it stays until the sessions
/// are revoked by the credential change.
/// </para>
/// <para>
/// Transient infrastructure errors are logged and never cause a sign-out.
/// </para>
/// </summary>
public static class SecurityStampCookieValidator
{
    public const string SecurityStampClaimType = "mrwho:sec_stamp";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("SecurityStampCookieValidator");

        if (await IsDeactivatedTenantUserAsync(context).ConfigureAwait(false))
        {
            // Deactivation is per tenant and leaves the account's credentials alone, so the stamp alone does not
            // cover a session issued after it (or one carrying no stamp).
            logger?.LogWarning("Rejected a session: the tenant user was deactivated");
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        string? currentStamp;
        var accountIdForLog = Guid.Empty;
        try
        {
            var db = context.HttpContext.RequestServices.GetService<AuthDbContext>();
            if (db is null)
            {
                return;
            }

            var resolved = await ResolveAccountAsync(db, context.Principal, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (resolved is null)
            {
                // Lenient: the account cannot be resolved, the other checks decide.
                return;
            }

            var (accountId, account) = resolved.Value;
            if (account is null)
            {
                // The account no longer exists: reject the session.
                logger?.LogWarning(
                    "Rejected a session: account {AccountId} no longer exists",
                    accountId);
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            accountIdForLog = accountId;
            currentStamp = account.SecurityStamp;
        }
        catch (Exception ex)
        {
            // Never sign a user out because of a transient DB/infrastructure error.
            logger?.LogWarning(ex, "SecurityStamp validation failed; session left intact");
            return;
        }

        // F1: the cookie carries a session, and the session is what a credential change revokes. The stamp is
        // not compared any more: the session check (which runs right after this one) rejects the cookie.
        if (HasSession(context))
        {
            return;
        }

        // The compatibility path: a cookie without a session is still checked against the stamp.
        var stampClaim = context.Principal?.FindFirst(SecurityStampClaimType);
        if (stampClaim is null || string.IsNullOrWhiteSpace(stampClaim.Value))
        {
            if (!string.IsNullOrWhiteSpace(currentStamp))
            {
                // The account has a stamp this cookie never carried: it was issued before a credential change.
                logger?.LogWarning(
                    "Rejected a session: account {AccountId} has a security stamp this cookie does not carry",
                    accountIdForLog);
                await RejectAsync(context).ConfigureAwait(false);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(currentStamp))
        {
            // The stamp was cleared (or the account has none): the cookie cannot be trusted.
            logger?.LogWarning(
                "Rejected a session: account {AccountId} has no security stamp",
                accountIdForLog);
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        var currentBytes = Encoding.UTF8.GetBytes(currentStamp);
        var cookieBytes = Encoding.UTF8.GetBytes(stampClaim.Value);
        if (!CryptographicOperations.FixedTimeEquals(currentBytes, cookieBytes))
        {
            // Stamp was rotated (password/email/MFA change) => stale cookie.
            logger?.LogWarning(
                "Rejected a session: the security stamp of account {AccountId} was rotated after the session was issued",
                accountIdForLog);
            await RejectAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when the cookie carries the id of an OP session (F1): the session, not the stamp, then decides whether
    /// the cookie is still valid.
    /// </summary>
    private static bool HasSession(CookieValidatePrincipalContext context)
    {
        var sid = context.Principal?.FindFirst(OidcConstants.Claims.Sid)?.Value;
        return !string.IsNullOrWhiteSpace(sid) && Guid.TryParse(sid, out _);
    }

    /// <summary>
    /// The account the cookie belongs to, with its security stamp.
    /// <para>
    /// The sign-in paths add the account id as an explicit claim. Without it, the per-tenant user id is used: it
    /// equals the account id in the home tenant, and in any other tenant the account is the one the user row is
    /// linked to.
    /// </para>
    /// </summary>
    private static async Task<(Guid AccountId, UserAccount? Account)?> ResolveAccountAsync(
        AuthDbContext db,
        ClaimsPrincipal? principal,
        CancellationToken ct)
    {
        // The claim that names the account directly (set by the sign-in paths): one query.
        if (Guid.TryParse(principal?.FindFirst(UserClaimTypes.UserAccountId)?.Value, out var claimed))
        {
            var account = await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == claimed, ct)
                .ConfigureAwait(false);
            return (claimed, account);
        }

        if (!Guid.TryParse(principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return null;
        }

        // The home tenant: the per-tenant user id is the account id.
        var byUserId = await db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == userId, ct)
            .ConfigureAwait(false);
        if (byUserId is not null)
        {
            return (userId, byUserId);
        }

        // Any other tenant: the account is the one the user row is linked to.
        var linked = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.UserAccountId ?? u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (linked == Guid.Empty)
        {
            return null;
        }

        var linkedAccount = await db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == linked, ct)
            .ConfigureAwait(false);
        return (linked, linkedAccount);
    }

    private static async Task<bool> IsDeactivatedTenantUserAsync(CookieValidatePrincipalContext context)
    {
        // NameIdentifier is the per-tenant User.Id (a platform session may carry an account id instead, which
        // matches no user row and so is never treated as deactivated). One primary-key lookup.
        if (!Guid.TryParse(context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return false;
        }

        try
        {
            var db = context.HttpContext.RequestServices.GetService<AuthDbContext>();
            return db is not null && await ActiveUserGate.IsDeactivatedAsync(db, userId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var loggerFactory = context.HttpContext.RequestServices.GetService<ILoggerFactory>();
            loggerFactory?.CreateLogger("SecurityStampCookieValidator")
                .LogWarning(ex, "User status validation failed; session left intact");
            return false;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
