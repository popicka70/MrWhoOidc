namespace LicensingService.Core.Services;

/// <summary>
/// Result of license token validation.
/// </summary>
public class LicenseValidationResult
{
    /// <summary>Whether the token is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Error message if validation failed.</summary>
    public string? Error { get; init; }

    /// <summary>Error code for programmatic handling.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Token ID (jti claim) if valid.</summary>
    public string? TokenId { get; init; }

    /// <summary>Customer identifier (sub claim) if valid.</summary>
    public string? CustomerIdentifier { get; init; }

    /// <summary>Product identifier (aud claim) if valid.</summary>
    public string? ProductIdentifier { get; init; }

    /// <summary>License tier if valid.</summary>
    public string? Tier { get; init; }

    /// <summary>License scope if valid.</summary>
    public string? Scope { get; init; }

    /// <summary>Token not-before date.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>Token expiration date.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>License options from token.</summary>
    public Dictionary<string, object>? Options { get; init; }

    /// <summary>Whether the license is currently within its validity period.</summary>
    public bool IsActive { get; init; }

    /// <summary>Days until expiration (negative if expired).</summary>
    public int? DaysUntilExpiry { get; init; }

    /// <summary>License status from database (if lookup performed).</summary>
    public string? DatabaseStatus { get; init; }

    /// <summary>Whether the license was revoked.</summary>
    public bool IsRevoked { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static LicenseValidationResult Success(
        string tokenId,
        string customerIdentifier,
        string productIdentifier,
        string tier,
        string scope,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        Dictionary<string, object>? options = null,
        string? databaseStatus = null,
        bool isRevoked = false)
    {
        var now = DateTimeOffset.UtcNow;
        var isActive = now >= validFrom && now <= validUntil && !isRevoked;
        var daysUntilExpiry = (int)(validUntil - now).TotalDays;

        return new LicenseValidationResult
        {
            IsValid = true,
            TokenId = tokenId,
            CustomerIdentifier = customerIdentifier,
            ProductIdentifier = productIdentifier,
            Tier = tier,
            Scope = scope,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Options = options,
            IsActive = isActive,
            DaysUntilExpiry = daysUntilExpiry,
            DatabaseStatus = databaseStatus,
            IsRevoked = isRevoked
        };
    }

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static LicenseValidationResult Failure(string error, string errorCode)
    {
        return new LicenseValidationResult
        {
            IsValid = false,
            Error = error,
            ErrorCode = errorCode,
            IsActive = false
        };
    }
}

/// <summary>
/// Service for validating license tokens.
/// </summary>
public interface ILicenseValidationService
{
    /// <summary>
    /// Validates a license token (signature, expiry, claims).
    /// </summary>
    /// <param name="token">The JWT license token.</param>
    /// <param name="checkDatabase">Whether to also check revocation status in database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LicenseValidationResult> ValidateAsync(string token, bool checkDatabase = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a license token for a specific product.
    /// </summary>
    /// <param name="token">The JWT license token.</param>
    /// <param name="expectedProductIdentifier">Expected product identifier (audience).</param>
    /// <param name="checkDatabase">Whether to also check revocation status in database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LicenseValidationResult> ValidateForProductAsync(string token, string expectedProductIdentifier, bool checkDatabase = false, CancellationToken cancellationToken = default);
}
