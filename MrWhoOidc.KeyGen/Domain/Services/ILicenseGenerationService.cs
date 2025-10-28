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
    /// <param name="features">Optional comma-separated list of enabled features</param>
    /// <param name="limits">Optional JSON object with resource limits (e.g., {"clients":10,"users":100})</param>
    /// <param name="createdBy">Username or identifier of the person generating the license</param>
    /// <returns>Tuple containing the tokenId (jti) and the signed JWT string</returns>
    Task<(string TokenId, string JwtToken)> GenerateLicenseTokenAsync(
        string tier,
        string organization,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        string? features = null,
        string? limits = null,
        string? createdBy = null);
}
