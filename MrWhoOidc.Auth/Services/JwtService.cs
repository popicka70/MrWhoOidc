using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Services;

public interface IJwtService
{
    string CreateJwt(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires);
}

internal sealed class JwtService(IKeyStore keyStore) : IJwtService
{
    public string CreateJwt(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires)
    {
        var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
        using var rsa = jwk.ToRSA();
        var key = new RsaSecurityKey(rsa) { KeyId = jwk.Kid };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires.UtcDateTime,
            signingCredentials: creds
        );

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(token);
    }
}
