namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Represents a generated license token with metadata for tracking purposes.
/// </summary>
/// <remarks>
/// This entity tracks license token metadata for audit trail purposes.
/// The actual JWT tokens are not stored, only metadata.
/// </remarks>
public class LicenseTokenMetadata
{
    /// <summary>
    /// Unique identifier for the license token metadata record (UUIDv7).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// JWT jti claim (unique token identifier).
    /// </summary>
    public required string TokenId { get; set; }

    /// <summary>
    /// License tier (community, professional, enterprise).
    /// </summary>
    public required string Tier { get; set; }

    /// <summary>
    /// License scope (platform or tenant).
    /// </summary>
    public required string Scope { get; set; }

    /// <summary>
    /// Organization name from license.
    /// </summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Display name for issued-to claim.
    /// </summary>
    public string? IssuedTo { get; set; }

    /// <summary>
    /// Tenant identifier when scope is tenant.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Optional tenant slug.
    /// </summary>
    public string? TenantSlug { get; set; }

    /// <summary>
    /// License validity start (nbf claim).
    /// </summary>
    public DateTimeOffset ValidFrom { get; set; }

    /// <summary>
    /// License validity end (exp claim).
    /// </summary>
    public DateTimeOffset ValidUntil { get; set; }

    /// <summary>
    /// JSON array of features (e.g., ["analytics","dpop","multi-tenant"]).
    /// </summary>
    public string? Features { get; set; }

    /// <summary>
    /// JSON array of features default tenant inherits from platform license.
    /// </summary>
    public string? DefaultTenantFeatures { get; set; }

    /// <summary>
    /// JSON object of limits (e.g., {"tenants":50,"users":1000}).
    /// </summary>
    public string? Limits { get; set; }

    /// <summary>
    /// When the license token was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// User/identity who generated the token (if auth is implemented).
    /// </summary>
    public string? GeneratedBy { get; set; }
}
