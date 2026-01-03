using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Protocols;
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
        var alg = key is JsonWebKey jwk && !string.IsNullOrWhiteSpace(jwk.Alg) ? jwk.Alg : SecurityConstants.JwtAlgorithms.RS256;
        var creds = new SigningCredentials(key, MapJwaToSecurityAlgorithms(alg));

        var header = new JwtHeader(creds);
        header["typ"] = "logout+jwt";

        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(header, payload);

        return handler.WriteToken(token);
    }

    private static string MapJwaToSecurityAlgorithms(string alg)
    {
        return alg.ToUpperInvariant() switch
        {
            SecurityConstants.JwtAlgorithms.RS256 => SecurityAlgorithms.RsaSha256,
            SecurityConstants.JwtAlgorithms.RS384 => SecurityAlgorithms.RsaSha384,
            SecurityConstants.JwtAlgorithms.RS512 => SecurityAlgorithms.RsaSha512,

            SecurityConstants.JwtAlgorithms.PS256 => SecurityAlgorithms.RsaSsaPssSha256,
            SecurityConstants.JwtAlgorithms.PS384 => SecurityAlgorithms.RsaSsaPssSha384,
            SecurityConstants.JwtAlgorithms.PS512 => SecurityAlgorithms.RsaSsaPssSha512,

            SecurityConstants.JwtAlgorithms.ES256 => SecurityAlgorithms.EcdsaSha256,
            SecurityConstants.JwtAlgorithms.ES384 => SecurityAlgorithms.EcdsaSha384,
            SecurityConstants.JwtAlgorithms.ES512 => SecurityAlgorithms.EcdsaSha512,

            _ => alg
        };
    }
}
