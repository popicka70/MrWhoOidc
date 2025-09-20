using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.Auth.Services;

public interface IClientAssertionValidator
{
    Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default);
}

public sealed class ClientAssertionValidator(AuthDbContext db, IConfiguration config) : IClientAssertionValidator
{
    public async Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
    {
        // Ensure client exists
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client == null) return false;

        // Load public JWK/JWKS for this client from configuration. Example configuration:
        // "Oidc": { "ClientAssertions": { "my-client": { "jwks": "{...}" } } }
        var jwkOrJwksJson =
            config[$"Oidc:ClientAssertions:{clientId}:jwks"] ??
            config[$"Oidc:ClientAssertions:{clientId}:jwk"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwks"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwk"];

        if (string.IsNullOrWhiteSpace(jwkOrJwksJson))
        {
            // No JWK configured => private_key_jwt not allowed for this client
            return false;
        }

        // Build signing keys from provided JWK/JWKS JSON
        IReadOnlyCollection<SecurityKey> signingKeys;
        try
        {
            if (jwkOrJwksJson.Contains("\"keys\"", StringComparison.Ordinal))
            {
                var set = new JsonWebKeySet(jwkOrJwksJson);
                signingKeys = set.Keys.Select(k => (SecurityKey)k).ToArray();
            }
            else
            {
                var jwk = new JsonWebKey(jwkOrJwksJson);
                signingKeys = new[] { (SecurityKey)jwk };
            }
        }
        catch
        {
            return false;
        }

        // Parse without validating to check custom constraints (iss/sub/jti)
        JwtSecurityToken jwt;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            jwt = handler.ReadJwtToken(assertion);
        }
        catch
        {
            return false;
        }

        // iss and sub must both equal client_id per RFC 7523
        var iss = jwt.Issuer;
        var sub = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        if (!string.Equals(iss, clientId, StringComparison.Ordinal) || !string.Equals(sub, clientId, StringComparison.Ordinal))
            return false;

        // jti must be present (uniqueness not enforced here)
        var jti = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti)) return false;

        // Validate signature, audience, lifetime
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = clientId,
            ValidateAudience = true,
            // Accept either the absolute token endpoint URL or issuer base + "/token"
            ValidAudiences = new[] { tokenEndpoint },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            // Restrict to common signature algs used for client assertions
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                                      SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512 }
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(assertion, tvp, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
