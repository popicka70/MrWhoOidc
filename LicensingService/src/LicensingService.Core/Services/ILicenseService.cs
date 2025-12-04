using LicensingService.Core.Entities;

namespace LicensingService.Core.Services;

/// <summary>
/// Request to issue a new license.
/// </summary>
public class IssueLicenseRequest
{
    /// <summary>Customer ID.</summary>
    public required Guid CustomerId { get; init; }

    /// <summary>Product ID.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>License tier (e.g., Community, Professional, Enterprise).</summary>
    public required string Tier { get; init; }

    /// <summary>License scope (e.g., site, tenant, global).</summary>
    public string Scope { get; init; } = "site";

    /// <summary>When the license becomes valid.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>When the license expires.</summary>
    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>Product-specific options.</summary>
    public Dictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Result of license issuance.
/// </summary>
public class IssueLicenseResult
{
    /// <summary>Whether the issuance succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The issued license (if successful).</summary>
    public License? License { get; init; }

    /// <summary>The signed JWT token (if successful).</summary>
    public string? Token { get; init; }

    /// <summary>Error message (if failed).</summary>
    public string? Error { get; init; }

    /// <summary>Error code for programmatic handling.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Validation errors for specific fields.</summary>
    public Dictionary<string, string[]>? ValidationErrors { get; init; }

    public static IssueLicenseResult Succeeded(License license, string token)
    {
        return new IssueLicenseResult
        {
            Success = true,
            License = license,
            Token = token
        };
    }

    public static IssueLicenseResult Failed(string error, string errorCode, Dictionary<string, string[]>? validationErrors = null)
    {
        return new IssueLicenseResult
        {
            Success = false,
            Error = error,
            ErrorCode = errorCode,
            ValidationErrors = validationErrors
        };
    }
}

/// <summary>
/// Request to renew an existing license.
/// </summary>
public class RenewLicenseRequest
{
    /// <summary>ID of the license to renew.</summary>
    public required Guid LicenseId { get; init; }

    /// <summary>New expiration date.</summary>
    public required DateTimeOffset NewValidUntil { get; init; }

    /// <summary>Optional option updates.</summary>
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// Request to revoke a license.
/// </summary>
public class RevokeLicenseRequest
{
    /// <summary>ID of the license to revoke.</summary>
    public required Guid LicenseId { get; init; }

    /// <summary>Reason for revocation.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Request to change license tier.
/// </summary>
public class ChangeTierRequest
{
    /// <summary>ID of the license to change.</summary>
    public required Guid LicenseId { get; init; }

    /// <summary>New tier.</summary>
    public required string NewTier { get; init; }

    /// <summary>Optional option updates for the new tier.</summary>
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// Service for managing the complete license lifecycle.
/// </summary>
public interface ILicenseService
{
    /// <summary>
    /// Issues a new license for a customer and product.
    /// </summary>
    Task<IssueLicenseResult> IssueLicenseAsync(IssueLicenseRequest request, string issuedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an existing license with 60-day overlap.
    /// </summary>
    Task<IssueLicenseResult> RenewLicenseAsync(RenewLicenseRequest request, string renewedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a license.
    /// </summary>
    Task<IssueLicenseResult> RevokeLicenseAsync(RevokeLicenseRequest request, string revokedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upgrades a license to a higher tier.
    /// </summary>
    Task<IssueLicenseResult> UpgradeLicenseAsync(ChangeTierRequest request, string upgradedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downgrades a license to a lower tier.
    /// </summary>
    Task<IssueLicenseResult> DowngradeLicenseAsync(ChangeTierRequest request, string downgradedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a license by ID with full details.
    /// </summary>
    Task<License?> GetLicenseAsync(Guid licenseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets licenses for a customer.
    /// </summary>
    Task<IReadOnlyList<License>> GetCustomerLicensesAsync(Guid customerId, LicenseStatus? status = null, CancellationToken cancellationToken = default);
}
