using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Services;

public interface ITokenValidator
{
    (bool ok, ClaimsPrincipal? principal, string? error) Validate(string token, string issuer);
}

internal sealed class TokenValidator(IKeyStore keyStore) : ITokenValidator
{
    public (bool ok, ClaimsPrincipal? principal, string? error) Validate(string token, string issuer)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };
        var keys = GetSigningKeys();
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            return (true, principal, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    IEnumerable<SecurityKey> GetSigningKeys()
    {
        // Include current and previous keys using JsonWebKey (public only) to avoid RSA disposal issues
        var jwks = keyStore.GetPublicJwksAsync().GetAwaiter().GetResult();
        foreach (var jwk in jwks)
        {
            yield return new JsonWebKey(jwk.ToJson(includePrivate: false));
        }
    }
}
