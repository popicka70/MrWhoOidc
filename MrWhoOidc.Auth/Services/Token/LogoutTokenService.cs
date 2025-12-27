using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services.KeyManagement;
using System.IdentityModel.Tokens.Jwt;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of ILogoutTokenService that creates RFC 8225 compliant logout tokens.
/// </summary>
public sealed class LogoutTokenService(ICachedKeyProvider keyProvider) : ILogoutTokenService
{
    /// <summary>
    /// Creates a logout_token JWT with the required claims per OIDC Back-Channel Logout spec.
    /// </summary>
    public async Task<string?> CreateLogoutTokenAsync(string issuer, string audienceClientId, string? sub, string? sid, CancellationToken ct = default)
    {
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

        var key = await keyProvider.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var header = new JwtHeader(creds);
        header["typ"] = "logout+jwt";

        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(header, payload);

        return handler.WriteToken(token);
    }
}
