using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.Auth.Services;

public interface IRequestObjectValidator
{
    Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string expectedAudience, CancellationToken ct = default);
}

public sealed class RequestObjectValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }

    public string? ClientId { get; init; }
    public AuthorizeRequest? Request { get; init; }
}

internal sealed class RequestObjectValidator(AuthDbContext db, IConfiguration config) : IRequestObjectValidator
{
    public async Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string expectedAudience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestJwt))
            return Invalid("invalid_request_object", "Missing request object");

        JwtSecurityToken unsigned;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            unsigned = handler.ReadJwtToken(requestJwt);
        }
        catch
        {
            return Invalid("invalid_request_object", "Malformed request object");
        }

        // Try to resolve client_id from claims (preferred claim: client_id, else iss)
        var clientId = unsigned.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value
                     ?? unsigned.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value
                     ?? unsigned.Issuer;

        if (string.IsNullOrWhiteSpace(clientId))
            return Invalid("invalid_request_object", "Missing client_id in request object");

        // Ensure the client exists
        var clientExists = await db.Clients.AsNoTracking().AnyAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (!clientExists)
            return Invalid("unauthorized_client", "Unknown client_id in request object");

        // Load public JWK/JWKS for this client from configuration.
        // Allow both RequestObjects and ClientAssertions sections for convenience.
        var jwkOrJwksJson =
            config[$"Oidc:RequestObjects:{clientId}:jwks"] ??
            config[$"Oidc:RequestObjects:{clientId}:jwk"] ??
            config[$"Auth:RequestObjects:{clientId}:jwks"] ??
            config[$"Auth:RequestObjects:{clientId}:jwk"] ??
            config[$"Oidc:ClientAssertions:{clientId}:jwks"] ??
            config[$"Oidc:ClientAssertions:{clientId}:jwk"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwks"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwk"];

        if (string.IsNullOrWhiteSpace(jwkOrJwksJson))
            return Invalid("invalid_request_object", "No JWK/JWKS configured for client");

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
            return Invalid("invalid_request_object", "Invalid JWK/JWKS configuration for client");
        }

        // Validate signature, audience, lifetime, issuer/subject (iss and sub == client_id)
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = clientId,
            ValidateAudience = true,
            ValidAudiences = new[] { expectedAudience },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = new[]
            {
                SecurityAlgorithms.RsaSha256,
                SecurityAlgorithms.EcdsaSha256
            }
        };

        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            principal = handler.ValidateToken(requestJwt, parameters, out _);
        }
        catch
        {
            return Invalid("invalid_request_object", "Signature or lifetime validation failed");
        }

        // iss and sub must equal client_id (defensive)
        var iss = principal.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value ?? unsigned.Issuer;
        var sub = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        if (!string.Equals(iss, clientId, StringComparison.Ordinal) || (sub != null && !string.Equals(sub, clientId, StringComparison.Ordinal)))
            return Invalid("invalid_request_object", "iss/sub mismatch");

        // Extract OpenID parameters from payload
        var payload = unsigned.Payload;
        var req = new AuthorizeRequest
        {
            response_type = payload.TryGetValue("response_type", out var rt) ? rt?.ToString() : null,
            client_id = clientId,
            redirect_uri = payload.TryGetValue("redirect_uri", out var ru) ? ru?.ToString() : null,
            scope = payload.TryGetValue("scope", out var sc) ? sc?.ToString() : null,
            state = payload.TryGetValue("state", out var st) ? st?.ToString() : null,
            nonce = payload.TryGetValue("nonce", out var no) ? no?.ToString() : null,
            code_challenge = payload.TryGetValue("code_challenge", out var cc) ? cc?.ToString() : null,
            code_challenge_method = payload.TryGetValue("code_challenge_method", out var ccm) ? ccm?.ToString() : null,
            resource = payload.TryGetValue("resource", out var res) ? res?.ToString() : null
        };

        return new RequestObjectValidationResult
        {
            IsValid = true,
            ClientId = clientId,
            Request = req
        };
    }

    static RequestObjectValidationResult Invalid(string code, string description) => new()
    {
        IsValid = false,
        Error = code,
        ErrorDescription = description
    };
}
