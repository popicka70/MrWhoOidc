using System.Text.Json.Serialization;

namespace LicensingService.Web.Models;

/// <summary>
/// Request to issue a new license.
/// </summary>
public class IssueLicenseRequest
{
    /// <summary>Customer ID to issue the license to.</summary>
    [JsonPropertyName("customerId")]
    public required Guid CustomerId { get; init; }

    /// <summary>Product ID to issue the license for.</summary>
    [JsonPropertyName("productId")]
    public required Guid ProductId { get; init; }

    /// <summary>License tier (e.g., "community", "professional", "enterprise").</summary>
    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    /// <summary>License scope (e.g., "per-user", "per-server", "unlimited").</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "default";

    /// <summary>License start date. Defaults to now.</summary>
    [JsonPropertyName("validFrom")]
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>License expiration date. Defaults to 1 year from start.</summary>
    [JsonPropertyName("validUntil")]
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>Product-specific options to include in the license.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Request to renew an existing license.
/// </summary>
public class RenewLicenseRequest
{
    /// <summary>New license start date. Supports overlap period.</summary>
    [JsonPropertyName("validFrom")]
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>New license expiration date.</summary>
    [JsonPropertyName("validUntil")]
    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>Optional option updates for the renewed license.</summary>
    [JsonPropertyName("optionUpdates")]
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// Request to revoke a license.
/// </summary>
public class RevokeLicenseRequest
{
    /// <summary>Reason for revocation.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>
/// Request to change license tier (upgrade or downgrade).
/// </summary>
public class ChangeTierRequest
{
    /// <summary>New tier for the license.</summary>
    [JsonPropertyName("newTier")]
    public required string NewTier { get; init; }

    /// <summary>Optional option updates for the new tier.</summary>
    [JsonPropertyName("optionUpdates")]
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// License response DTO.
/// </summary>
public class LicenseResponse
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("tokenId")]
    public required string TokenId { get; init; }

    [JsonPropertyName("customerId")]
    public required Guid CustomerId { get; init; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("customerIdentifier")]
    public string? CustomerIdentifier { get; init; }

    [JsonPropertyName("productId")]
    public required Guid ProductId { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    [JsonPropertyName("productIdentifier")]
    public string? ProductIdentifier { get; init; }

    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("validFrom")]
    public required DateTimeOffset ValidFrom { get; init; }

    [JsonPropertyName("validUntil")]
    public required DateTimeOffset ValidUntil { get; init; }

    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; init; }

    [JsonPropertyName("revocationReason")]
    public string? RevocationReason { get; init; }

    [JsonPropertyName("renewedFromId")]
    public Guid? RenewedFromId { get; init; }
}

/// <summary>
/// License with token response (returned after issuance).
/// </summary>
public class LicenseWithTokenResponse : LicenseResponse
{
    /// <summary>Signed JWT license token.</summary>
    [JsonPropertyName("signedToken")]
    public required string SignedToken { get; init; }
}

/// <summary>
/// License event response DTO.
/// </summary>
public class LicenseEventResponse
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("licenseId")]
    public required Guid LicenseId { get; init; }

    [JsonPropertyName("eventType")]
    public required string EventType { get; init; }

    [JsonPropertyName("performedBy")]
    public required string PerformedBy { get; init; }

    [JsonPropertyName("performedAt")]
    public required DateTimeOffset PerformedAt { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }
}

/// <summary>
/// License search request with filters.
/// </summary>
public class LicenseSearchRequest
{
    [JsonPropertyName("customerId")]
    public Guid? CustomerId { get; init; }

    [JsonPropertyName("productId")]
    public Guid? ProductId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("tier")]
    public string? Tier { get; init; }

    [JsonPropertyName("validAt")]
    public DateTimeOffset? ValidAt { get; init; }

    [JsonPropertyName("searchText")]
    public string? SearchText { get; init; }

    [JsonPropertyName("skip")]
    public int Skip { get; init; } = 0;

    [JsonPropertyName("take")]
    public int Take { get; init; } = 50;
}

/// <summary>
/// Paginated license search result.
/// </summary>
public class LicenseSearchResponse
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<LicenseResponse> Items { get; init; }

    [JsonPropertyName("totalCount")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("skip")]
    public required int Skip { get; init; }

    [JsonPropertyName("take")]
    public required int Take { get; init; }
}

/// <summary>
/// Request to bulk renew multiple licenses.
/// </summary>
public class BulkRenewRequest
{
    /// <summary>List of license IDs to renew.</summary>
    [JsonPropertyName("licenseIds")]
    public required IReadOnlyList<Guid> LicenseIds { get; init; }

    /// <summary>New expiration date for all renewed licenses.</summary>
    [JsonPropertyName("validUntil")]
    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>Optional option updates for all renewed licenses.</summary>
    [JsonPropertyName("optionUpdates")]
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// Request to bulk revoke multiple licenses.
/// </summary>
public class BulkRevokeRequest
{
    /// <summary>List of license IDs to revoke.</summary>
    [JsonPropertyName("licenseIds")]
    public required IReadOnlyList<Guid> LicenseIds { get; init; }

    /// <summary>Reason for revocation (applies to all licenses).</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>
/// Response from a bulk license operation.
/// </summary>
public class BulkOperationResponse
{
    /// <summary>Total number of licenses in the request.</summary>
    [JsonPropertyName("totalRequested")]
    public required int TotalRequested { get; init; }

    /// <summary>Number of successfully processed licenses.</summary>
    [JsonPropertyName("successCount")]
    public required int SuccessCount { get; init; }

    /// <summary>Number of failed operations.</summary>
    [JsonPropertyName("failureCount")]
    public required int FailureCount { get; init; }

    /// <summary>Details of successful operations.</summary>
    [JsonPropertyName("successes")]
    public required IReadOnlyList<BulkSuccessItem> Successes { get; init; }

    /// <summary>Details of failed operations.</summary>
    [JsonPropertyName("failures")]
    public required IReadOnlyList<BulkFailureItem> Failures { get; init; }
}

/// <summary>
/// Details of a successful bulk operation item.
/// </summary>
public class BulkSuccessItem
{
    /// <summary>Original license ID.</summary>
    [JsonPropertyName("originalLicenseId")]
    public required Guid OriginalLicenseId { get; init; }

    /// <summary>New license ID (for renewal).</summary>
    [JsonPropertyName("newLicenseId")]
    public Guid? NewLicenseId { get; init; }

    /// <summary>New license token (for renewal).</summary>
    [JsonPropertyName("newToken")]
    public string? NewToken { get; init; }
}

/// <summary>
/// Details of a failed bulk operation item.
/// </summary>
public class BulkFailureItem
{
    /// <summary>License ID that failed.</summary>
    [JsonPropertyName("licenseId")]
    public required Guid LicenseId { get; init; }

    /// <summary>Error message.</summary>
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    /// <summary>Error code.</summary>
    [JsonPropertyName("errorCode")]
    public required string ErrorCode { get; init; }
}
