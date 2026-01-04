using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services.Authorization;

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

public interface IJarReplayCache
{
    // Returns true when the key was added (not present), false when a non-expired entry already existed
    bool TryAdd(string key, DateTimeOffset expiresAt);
}

internal sealed class RequestObjectValidator(AuthDbContext db, IConfiguration config, ILogger<RequestObjectValidator> logger, IOptions<AuthOptions> authOptions, IJarReplayCache replayCache, IRequestObjectDecryptor requestObjectDecryptor) : IRequestObjectValidator
{
    public async Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string expectedAudience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestJwt))
            return Invalid("invalid_request_object", "Missing request object");

        // If this is a JWE, decrypt it to the inner (nested) signed JWT first.
        // We require nested JWT so the request object still has client-authenticity via signature.
        if (requestJwt.Split('.').Length == 5)
        {
            try
            {
                var inner = await requestObjectDecryptor.TryDecryptToInnerJwtAsync(requestJwt, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(inner))
                {
                    logger.LogWarning("JAR: encrypted request object did not contain a nested JWT");
                    return Invalid("invalid_request_object", "Encrypted request object missing nested JWT");
                }

                requestJwt = inner;
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "JAR: unsupported request object encryption");
                return Invalid("invalid_request_object", "Unsupported request object encryption");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JAR: failed to decrypt request object");
                return Invalid("invalid_request_object", "Failed to decrypt request object");
            }
        }

        JwtSecurityToken unsigned;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            unsigned = handler.ReadJwtToken(requestJwt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JAR: malformed request object");
            return Invalid("invalid_request_object", "Malformed request object");
        }

        // Lifetime hardening: enforce max lifetime window exp - (nbf or iat)
        var opts = authOptions.Value;
        if (opts.RequestObjectMaxLifetimeSeconds > 0)
        {
            try
            {
                long? ReadLong(object? o)
                    => o is null ? null : (o is long l ? l : (long.TryParse(o.ToString(), out var v) ? v : null));

                var payload = unsigned.Payload;
                payload.TryGetValue("exp", out var expObj);
                payload.TryGetValue("nbf", out var nbfObj);
                payload.TryGetValue("iat", out var iatObj);
                var exp = ReadLong(expObj);
                var nbf = ReadLong(nbfObj);
                var iat = ReadLong(iatObj);
                var start = nbf ?? iat;
                if (exp is not null && start is not null)
                {
                    var window = exp.Value - start.Value;
                    var skew = opts.RequestObjectClockSkewSeconds > 0 ? opts.RequestObjectClockSkewSeconds : 120;
                    if (window > opts.RequestObjectMaxLifetimeSeconds + skew)
                    {
                        logger.LogWarning("JAR: request object lifetime too long (window={Window}s, max={Max}s)", window, opts.RequestObjectMaxLifetimeSeconds);
                        return Invalid("invalid_request_object", "Request object lifetime too long");
                    }
                }
            }
            catch
            {
                // ignore lifetime parsing errors; validation below will catch invalid times
            }
        }

        // Try to resolve client_id from claims (preferred claim: client_id, else iss)
        var clientId = unsigned.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value
                     ?? unsigned.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value
                     ?? unsigned.Issuer;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("JAR: missing client_id in request object");
            return Invalid("invalid_request_object", "Missing client_id in request object");
        }

        // Ensure the client exists
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            logger.LogWarning("JAR: unknown client_id {ClientId}", clientId);
            return Invalid("unauthorized_client", "Unknown client_id in request object");
        }

        // Build signing keys from DB-stored JWKS/JWK first, then fall back to configuration.
        string? jwkOrJwksJson = client.PublicJwksJson;
        jwkOrJwksJson ??=
            config[$"Oidc:RequestObjects:{clientId}:jwks"] ??
            config[$"Oidc:RequestObjects:{clientId}:jwk"] ??
            config[$"Auth:RequestObjects:{clientId}:jwks"] ??
            config[$"Auth:RequestObjects:{clientId}:jwk"] ??
            config[$"Oidc:ClientAssertions:{clientId}:jwks"] ??
            config[$"Oidc:ClientAssertions:{clientId}:jwk"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwks"] ??
            config[$"Auth:ClientAssertions:{clientId}:jwk"];

        if (string.IsNullOrWhiteSpace(jwkOrJwksJson))
        {
            logger.LogWarning("JAR: no JWK/JWKS configured for client {ClientId}", clientId);
            return Invalid("invalid_request_object", "No JWK/JWKS configured for client");
        }

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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JAR: invalid JWK/JWKS configuration for client {ClientId}", clientId);
            return Invalid("invalid_request_object", "Invalid JWK/JWKS configuration for client");
        }

        // Determine allowed alg set (global allow-list + per-client override)
        var allowedAlgs = opts.RequestObjectAllowedAlgorithmsPerClient.TryGetValue(clientId, out var perClient)
            ? perClient
            : opts.RequestObjectAllowedAlgorithms;

        // Map to IdentityModel algs for TokenValidationParameters.ValidAlgorithms
        static string MapAlg(string alg) => alg switch
        {
            "RS256" => SecurityAlgorithms.RsaSha256,
            "PS256" => SecurityAlgorithms.RsaSsaPssSha256,
            "ES256" => SecurityAlgorithms.EcdsaSha256,
            "ES384" => SecurityAlgorithms.EcdsaSha384,
            "ES512" => SecurityAlgorithms.EcdsaSha512,
            _ => alg
        };
        var validAlgs = allowedAlgs.Select(MapAlg).ToArray();

        // Validate signature, audience, lifetime, issuer/subject (iss and sub == client_id) with allowed algs
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = clientId,
            ValidateAudience = true,
            ValidAudiences = new[] { expectedAudience },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(authOptions.Value.RequestObjectClockSkewSeconds > 0 ? authOptions.Value.RequestObjectClockSkewSeconds : 120),
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = validAlgs
        };

        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            principal = handler.ValidateToken(requestJwt, parameters, out _);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JAR: signature or lifetime validation failed for client {ClientId}", clientId);
            return Invalid("invalid_request_object", "Signature or lifetime validation failed");
        }

        // iss and sub must equal client_id (defensive)
        var iss = principal.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value ?? unsigned.Issuer;
        var sub = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        if (!string.Equals(iss, clientId, StringComparison.Ordinal) || (sub != null && !string.Equals(sub, clientId, StringComparison.Ordinal)))
        {
            logger.LogWarning("JAR: iss/sub mismatch for client {ClientId} (iss={Iss}, sub={Sub})", clientId, iss, sub);
            return Invalid("invalid_request_object", "iss/sub mismatch");
        }

        // Replay protection: jti (preferred) or nonce, with TTL derived from exp or configured default
        var jti = unsigned.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrEmpty(jti) && unsigned.Payload.TryGetValue("jti", out var jtiObjRaw))
        {
            jti = jtiObjRaw?.ToString();
        }
        var nonce = unsigned.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
        if (string.IsNullOrEmpty(nonce) && unsigned.Payload.TryGetValue("nonce", out var nonceObjRaw))
        {
            nonce = nonceObjRaw?.ToString();
        }
        var keyId = !string.IsNullOrEmpty(jti) ? jti : (!string.IsNullOrEmpty(nonce) ? $"nonce:{nonce}" : null);
        if (!string.IsNullOrEmpty(keyId))
        {
            long? ReadLong2(object? o)
                => o is null ? null : (o is long l ? l : (long.TryParse(o.ToString(), out var v) ? v : null));
            unsigned.Payload.TryGetValue("exp", out var expObj2);
            var exp2 = ReadLong2(expObj2);
            var now = DateTimeOffset.UtcNow;
            var ttl = exp2 is not null ? DateTimeOffset.FromUnixTimeSeconds(exp2.Value + (opts.RequestObjectClockSkewSeconds > 0 ? opts.RequestObjectClockSkewSeconds : 120)) - now
                                       : TimeSpan.FromSeconds(Math.Max(60, opts.RequestObjectReplayTtlSeconds));
            if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromSeconds(60);
            var expiresAt = now.Add(ttl);
            // Include issuer (clientId) + audience + jti/nonce to scope replay keys across different audiences
            var replayKey = $"jar:{clientId}:{expectedAudience}:{keyId}";
            if (!replayCache.TryAdd(replayKey, expiresAt))
            {
                logger.LogWarning("JAR: replay detected for client {ClientId} key {KeyId}", clientId, keyId);
                return Invalid("invalid_request_object", "Replay detected (jti/nonce) ");
            }
        }

        // Extract OpenID parameters from payload
        var payload2 = unsigned.Payload;
        var req = new AuthorizeRequest(
            response_type: payload2.TryGetValue("response_type", out var rt) ? rt?.ToString() : null,
            client_id: clientId,
            redirect_uri: payload2.TryGetValue("redirect_uri", out var ru) ? ru?.ToString() : null,
            scope: payload2.TryGetValue("scope", out var sc) ? sc?.ToString() : null,
            state: payload2.TryGetValue("state", out var st) ? st?.ToString() : null,
            nonce: payload2.TryGetValue("nonce", out var no) ? no?.ToString() : null,
            code_challenge: payload2.TryGetValue("code_challenge", out var cc) ? cc?.ToString() : null,
            code_challenge_method: payload2.TryGetValue("code_challenge_method", out var ccm) ? ccm?.ToString() : null,
            resource: payload2.TryGetValue("resource", out var res) ? res?.ToString() : null,
            response_mode: payload2.TryGetValue("response_mode", out var rm) ? rm?.ToString() : null
        );

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
