using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Result of ID token validation.
/// </summary>
public sealed class TokenValidationResult
{
    public bool Success { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public string? Acr { get; init; }
    public string[] Amrs { get; init; } = Array.Empty<string>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Validates ID tokens from external OIDC providers.
/// </summary>
public interface IExternalOidcTokenValidator
{
    Task<TokenValidationResult> ValidateIdTokenAsync(
        string idToken,
        string jwksUri,
        string? expectedIssuer,
        string expectedAudience,
        string? expectedNonce,
        CancellationToken cancellationToken);
}

internal sealed class ExternalOidcTokenValidator : IExternalOidcTokenValidator
{
    private readonly IJwksCache _jwksCache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExternalOidcTokenValidator> _logger;

    public ExternalOidcTokenValidator(
        IJwksCache jwksCache,
        IHttpClientFactory httpFactory,
        ILogger<ExternalOidcTokenValidator> logger)
    {
        _jwksCache = jwksCache;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<TokenValidationResult> ValidateIdTokenAsync(
        string idToken,
        string jwksUri,
        string? expectedIssuer,
        string expectedAudience,
        string? expectedNonce,
        CancellationToken cancellationToken)
    {
        try
        {
            var set = await _jwksCache.GetAsync(jwksUri, TimeSpan.FromMinutes(15), _httpFactory, cancellationToken);
            if (set is null)
            {
                _logger.LogWarning("JWKS fetch failed for {JwksUri}", jwksUri);
                return new TokenValidationResult
                {
                    Success = false,
                    ErrorCode = "jwks_failed",
                    ErrorMessage = "JWKS fetch failed"
                };
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            tokenHandler.InboundClaimTypeMap.Clear();

            var parms = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = expectedIssuer,
                ValidateAudience = true,
                ValidAudience = expectedAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = set.Keys,
                NameClaimType = "name",
                RoleClaimType = "roles"
            };

            var principal = tokenHandler.ValidateToken(idToken, parms, out var _);

            var issuer = principal.FindFirst("iss")?.Value ?? parms.ValidIssuer;
            var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var name = principal.FindFirst("name")?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value;
            var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
            var acr = principal.FindFirst("acr")?.Value;
            var amrs = principal.Claims
                .Where(c => c.Type == "amr")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var nonceClaim = principal.FindFirst("nonce")?.Value;
            if (!string.IsNullOrEmpty(expectedNonce) &&
                !string.Equals(expectedNonce, nonceClaim, StringComparison.Ordinal))
            {
                _logger.LogWarning("Nonce mismatch in ID token validation");
                return new TokenValidationResult
                {
                    Success = false,
                    ErrorCode = "nonce_mismatch",
                    ErrorMessage = "Nonce mismatch"
                };
            }

            return new TokenValidationResult
            {
                Success = true,
                Subject = sub,
                Issuer = issuer,
                Email = email,
                Name = name,
                Acr = acr,
                Amrs = amrs
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ID token validation failed");
            return new TokenValidationResult
            {
                Success = false,
                ErrorCode = "id_token_validation_failed",
                ErrorMessage = $"ID token validation failed: {ex.Message}"
            };
        }
    }
}
