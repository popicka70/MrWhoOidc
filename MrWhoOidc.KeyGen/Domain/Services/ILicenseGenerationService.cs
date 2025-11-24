namespace MrWhoOidc.KeyGen.Domain.Services;

/// <summary>
/// Service interface for generating license tokens.
/// </summary>
public interface ILicenseGenerationService
{
    /// <summary>
    /// Generates a signed JWT license token with the specified parameters.
    /// </summary>
    /// <param name="tier">License tier (Free, Developer, Pro, Enterprise)</param>
    /// <param name="organization">Organization name</param>
    /// <param name="notBefore">Not valid before date (nbf claim)</param>
    /// <param name="expiresAt">Expiration date (exp claim)</param>
    /// <param name="scope">License scope (platform or tenant)</param>
    /// <param name="issuedTo">Optional subject/recipient string</param>
    /// <param name="tenantId">Tenant identifier when scope is tenant</param>
    /// <param name="tenantSlug">Friendly tenant slug when scope is tenant</param>
    /// <param name="features">Optional JSON array of enabled features</param>
    /// <param name="limits">Optional JSON object with resource limits (e.g., {"clients":10,"users":100})</param>
    /// <param name="createdBy">Username or identifier of the person generating the license</param>
    /// <param name="defaultTenantFeatures">Optional JSON array of features default tenant should inherit (platform scope only)</param>
    /// <param name="allowedIssuers">Optional JSON array of allowed issuers</param>
    /// <returns>Tuple containing the tokenId (jti) and the signed JWT string</returns>
    Task<(string TokenId, string JwtToken)> GenerateLicenseTokenAsync(
        string tier,
        string organization,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        string scope,
        string? issuedTo = null,
        Guid? tenantId = null,
        string? tenantSlug = null,
        string? features = null,
        string? limits = null,
        string? createdBy = null,
        string? defaultTenantFeatures = null,
        string? allowedIssuers = null);
}
