using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

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
