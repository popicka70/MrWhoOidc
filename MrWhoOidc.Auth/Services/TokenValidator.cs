using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for validating JWT tokens.
/// </summary>
public interface ITokenValidator
{
    /// <summary>
    /// Validates a JWT token against the issuer's public keys.
    /// </summary>
    /// <param name="token">The JWT token to validate.</param>
    /// <param name="issuer">The expected issuer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="validAudiences">Expected audiences. Audience validation is mandatory; pass a non-empty list unless the caller performs an equivalent audience check elsewhere.</param>
    /// <param name="skipAudienceValidation">Explicit opt-out of audience validation. Only for callers that validate the audience themselves after this call (e.g. token exchange).</param>
    /// <returns>A result containing the success status, the validated principal, and any error message.</returns>
    Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default, IEnumerable<string>? validAudiences = null, bool skipAudienceValidation = false);
}

internal sealed class TokenValidator(
    ICachedKeyProvider keyProvider,
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    ILogger<TokenValidator> logger,
    IOptions<AuthOptions> authOptions) : ITokenValidator
{
    public async Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default, IEnumerable<string>? validAudiences = null, bool skipAudienceValidation = false)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        var keys = await keyProvider.GetPublicJwksAsync(ct).ConfigureAwait(false);
        var expectedAudiences = validAudiences?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var clockSkew = TimeSpan.FromSeconds(authOptions.Value.TokenValidationClockSkewSeconds);

        if (expectedAudiences.Length == 0)
        {
            if (!skipAudienceValidation)
            {
                throw new SecurityTokenInvalidAudienceException(
                    "Audience validation was requested but no audiences were supplied.");
            }

            logger.LogWarning("TokenValidator.ValidateAsync called with skipAudienceValidation: true — audience validation is skipped. Caller must perform post-validation audience checks.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            // Audience validation is mandatory by default. skipAudienceValidation is an explicit
            // opt-out for callers that perform their own audience checks after this call.
            ValidateAudience = !skipAudienceValidation && expectedAudiences.Length > 0,
            RequireAudience = !skipAudienceValidation && expectedAudiences.Length > 0,
            ValidAudiences = expectedAudiences.Length > 0 ? expectedAudiences : null,
            ValidateLifetime = true,
            ClockSkew = clockSkew,
            NameClaimType = "sub",
            RoleClaimType = "role",
            // Pin allowed signature algorithms to asymmetric (RSA/ECDSA) only.
            // Defense-in-depth against algorithm-confusion (e.g. HS256 using a public key as the HMAC secret).
            ValidAlgorithms = new[]
            {
                SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
            }
        };
        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);

            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                ?? principal.FindFirst("jti")?.Value;
            var tokenHash = CryptoHelper.ComputeSha256Base64(token);

            var revokedTokens = db.Tokens
                .AsNoTracking()
                .Where(t => t.Type == "access" && t.RevokedAt != null);

            var tenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId.HasValue && tenantId.Value != Guid.Empty)
            {
                revokedTokens = revokedTokens.Where(t => t.TenantId == tenantId.Value);
            }

            if (await revokedTokens.Where(t => t.TokenHash == tokenHash).AnyAsync(ct).ConfigureAwait(false))
            {
                return (false, null, "token_revoked");
            }

            if (!string.IsNullOrWhiteSpace(jti)
                && await revokedTokens.Where(t => t.Jti == jti).AnyAsync(ct).ConfigureAwait(false))
            {
                return (false, null, "token_revoked");
            }

            return (true, principal, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token validation failed for issuer {Issuer}", issuer);
            return (false, null, "token_validation_failed");
        }
    }
}
