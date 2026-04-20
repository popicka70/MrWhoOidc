using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

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
    /// <returns>A result containing the success status, the validated principal, and any error message.</returns>
    Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default);
}

internal sealed class TokenValidator(
    ICachedKeyProvider keyProvider,
    AuthDbContext db,
    ITenantAccessor tenantAccessor) : ITokenValidator
{
    public async Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        var keys = await keyProvider.GetPublicJwksAsync(ct).ConfigureAwait(false);

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

            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                ?? principal.FindFirst("jti")?.Value;
            var tokenHash = CryptoHelper.ComputeSha256Base64(token);

            var revokedQuery = db.Tokens
                .AsNoTracking()
                .Where(t => t.Type == "access" && t.RevokedAt != null)
                .Where(t => t.TokenHash == tokenHash || (!string.IsNullOrWhiteSpace(jti) && t.Jti == jti));

            var tenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId.HasValue && tenantId.Value != Guid.Empty)
            {
                revokedQuery = revokedQuery.Where(t => t.TenantId == tenantId.Value);
            }

            if (await revokedQuery.AnyAsync(ct).ConfigureAwait(false))
            {
                return (false, null, "token_revoked");
            }

            return (true, principal, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
