using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Manages user session establishment after external authentication.
/// </summary>
public interface IExternalOidcSessionManager
{
    Task SignInAsync(
        HttpContext http,
        Guid userId,
        string? name,
        string? email,
        string? idp,
        string? acr,
        string[] amrs,
        IReadOnlyDictionary<string, string> mappedClaims,
        string? rawIdToken);

    /// <summary>
    /// Enforces the same MFA gate as password login (see Login.cshtml.cs / WebAuthnHandler.cs)
    /// BEFORE the main auth cookie is issued. When the tenant requires MFA (and the user has no
    /// TOTP) or the user has TOTP enabled, issues the short-lived preauth cookie and returns the
    /// redirect target (TOTP challenge or MFA enrollment page). Returns null when no MFA step is
    /// required and the caller may proceed to sign the user in.
    /// </summary>
    Task<string?> GetMfaRedirectIfRequiredAsync(HttpContext http, Guid userId, string? name, string? email, string? returnUrl);

    void SetLastProviderCookie(HttpContext http, string provider, string? clientId);
}

internal sealed class ExternalOidcSessionManager : IExternalOidcSessionManager
{
    public async Task SignInAsync(
        HttpContext http,
        Guid userId,
        string? name,
        string? email,
        string? idp,
        string? acr,
        string[] amrs,
        IReadOnlyDictionary<string, string> mappedClaims,
        string? rawIdToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name ?? email ?? $"{idp}:user"),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (!string.IsNullOrEmpty(idp))
            claims.Add(new("idp", idp));

        if (!string.IsNullOrEmpty(acr))
            claims.Add(new("acr", acr));

        if (amrs is { Length: > 0 })
        {
            foreach (var v in amrs)
                claims.Add(new("amr", v));
        }

        if (mappedClaims is { Count: > 0 })
        {
            foreach (var kv in mappedClaims)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    claims.Add(new($"ext_map_{kv.Key}", kv.Value));
                }
            }
        }

        // Bind the auth cookie to the user's current SecurityStamp (stored on the global
        // UserAccount, linked via the per-tenant User's email) so credential changes (password
        // reset, MFA disable, deactivation) invalidate existing external-IdP sessions.
        var db = http.RequestServices.GetService<AuthDbContext>();
        var accountService = http.RequestServices.GetService<IUserAccountService>();
        if (db is not null && accountService is not null)
        {
            var tenantUser = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (!string.IsNullOrEmpty(tenantUser?.Email))
            {
                var account = await accountService.FindByEmailAsync(tenantUser.Email);
                if (!string.IsNullOrEmpty(account?.SecurityStamp))
                    claims.Add(new("mrwho:sec_stamp", account.SecurityStamp));
            }
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var props = new AuthenticationProperties();

        if (!string.IsNullOrEmpty(rawIdToken))
        {
            try
            {
                var dp = http.RequestServices.GetRequiredService<IDataProtectionProvider>();
                var protector = dp.CreateProtector("federated-logout-idtoken");
                props.Items["UpstreamIdTokenEnc"] = protector.Protect(rawIdToken);
            }
            catch
            {
                // Non-fatal
            }

            if (rawIdToken.Count(c => c == '.') == 2)
            {
                try
                {
                    var sidVal = MrWhoOidc.WebAuth.Infrastructure.JwtLightParser.TryGetClaim(rawIdToken, "sid");
                    if (!string.IsNullOrEmpty(sidVal))
                        props.Items["UpstreamSid"] = sidVal;
                }
                catch
                {
                    // Non-fatal
                }
            }
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
    }

    /// <summary>
    /// Enforces the same MFA gate as password login (see Login.cshtml.cs / WebAuthnHandler.cs)
    /// BEFORE the main auth cookie is issued. A preauth-only session must not satisfy flows that
    /// require a fully-authenticated (post-MFA) session.
    /// Returns a redirect URL when the caller must send the user to the TOTP challenge
    /// (/LoginTotp) or MFA enrollment (/Mfa/Index) first; null when the user may be signed in.
    /// </summary>
    public async Task<string?> GetMfaRedirectIfRequiredAsync(HttpContext http, Guid userId, string? name, string? email, string? returnUrl)
    {
        var settingsService = http.RequestServices.GetService<ITenantSettingsService>();
        var settings = settingsService is not null
            ? await settingsService.GetCurrentTenantSettingsAsync()
            : new TenantSettings();

        var hasTotp = await UserHasTotpAsync(http, userId);
        if ((settings.Auth?.RequireMfa ?? false) && !hasTotp)
        {
            await IssuePreauthAsync(http, userId, name ?? email, enrollmentRequired: true);
            var enrollUrl = $"/Mfa/Index?required=true";
            if (!string.IsNullOrEmpty(returnUrl))
                enrollUrl += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
            return enrollUrl;
        }

        if (hasTotp)
        {
            await IssuePreauthAsync(http, userId, name ?? email, enrollmentRequired: false);
            return !string.IsNullOrEmpty(returnUrl)
                ? $"/LoginTotp?ReturnUrl={Uri.EscapeDataString(returnUrl)}"
                : "/LoginTotp";
        }

        return null;
    }

    private async Task<bool> UserHasTotpAsync(HttpContext http, Guid userId)
    {
        var db = http.RequestServices.GetService<AuthDbContext>();
        if (db is null)
            return false;

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
        return user?.TotpEnabled ?? false;
    }

    private async Task IssuePreauthAsync(HttpContext http, Guid userId, string? userName, bool enrollmentRequired)
    {
        var preauthClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userName ?? userId.ToString()),
            new("amr", "ext")
        };
        if (enrollmentRequired)
            preauthClaims.Add(new("mfa_enrollment_required", "true"));

        var preauthIdentity = new ClaimsIdentity(preauthClaims, "preauth");
        await http.SignInAsync("preauth", new ClaimsPrincipal(preauthIdentity));
    }

    public void SetLastProviderCookie(HttpContext http, string provider, string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            return;

        var cookieName = "__Host-mrwhooidc-lastidp-" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))
            .Substring(0, 16);

        var insecureForTests = http.RequestServices.GetService<IConfiguration>()
            ?.GetValue<bool>("Testing:InsecureCookies") ?? false;

        http.Response.Cookies.Append(cookieName, provider, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddDays(90),
            SameSite = SameSiteMode.Lax,
            Secure = !insecureForTests,
            HttpOnly = true,
            IsEssential = true,
            Path = "/"
        });
    }
}
