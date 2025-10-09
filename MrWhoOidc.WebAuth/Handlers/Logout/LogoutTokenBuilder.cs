using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Infrastructure;
using System.IdentityModel.Tokens.Jwt;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Creates RFC 8225 compliant logout tokens for back-channel logout.
/// </summary>
public sealed class LogoutTokenBuilder(IKeyStore keyStore)
{
    /// <summary>
    /// Creates a logout_token JWT with the required claims per OIDC Back-Channel Logout spec.
    /// Returns null if neither sub nor sid can be included (spec requires at least one).
    /// </summary>
    public string? CreateLogoutToken(string issuer, string audienceClientId, string? idTokenHint, string? sidFromQuery)
    {
        var sub = idTokenHint != null ? JwtLightParser.TryGetClaim(idTokenHint, "sub") : null;
        var sid = !string.IsNullOrEmpty(sidFromQuery) ? sidFromQuery : (idTokenHint != null ? JwtLightParser.TryGetClaim(idTokenHint, "sid") : null);

        if (string.IsNullOrEmpty(sub) && string.IsNullOrEmpty(sid))
        {
            // Spec requires at least one of sid or sub
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var payload = new JwtPayload
        {
            { "iss", issuer },
            { "aud", audienceClientId },
            { "iat", now },
            { "exp", exp },
            { "jti", Guid.NewGuid().ToString("N") },
            { "events", new Dictionary<string, object>
                {
                    { "http://schemas.openid.net/event/backchannel-logout", new Dictionary<string, object>() }
                }
            }
        };

        if (!string.IsNullOrEmpty(sub)) payload["sub"] = sub;
        if (!string.IsNullOrEmpty(sid)) payload["sid"] = sid;

        var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
        var jsonWebKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
        var creds = new SigningCredentials(jsonWebKey, SecurityAlgorithms.RsaSha256);

        var header = new JwtHeader(creds)
        {
            { "typ", "logout+jwt" }
        };

        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(header, payload);

        return handler.WriteToken(token);
    }
}
