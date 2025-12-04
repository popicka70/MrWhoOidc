namespace LicensingService.Core.Entities;

/// <summary>
/// Represents an issued license token.
/// </summary>
public class License
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>JWT jti claim - unique token identifier.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>The signed JWT token.</summary>
    public string SignedToken { get; set; } = string.Empty;

    /// <summary>The kid of the signing key used.</summary>
    public string SigningKeyId { get; set; } = string.Empty;

    /// <summary>Reference to the customer.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>Reference to the licensed product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>License tier (Community, Professional, Enterprise, etc.).</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>License scope (platform, tenant, etc.).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Not-before date (nbf) - when the license becomes valid.</summary>
    public DateTimeOffset ValidFrom { get; set; }

    /// <summary>Expiration date (exp) - when the license expires.</summary>
    public DateTimeOffset ValidUntil { get; set; }

    /// <summary>Product options as key-value JSON.</summary>
    public string? Options { get; set; }

    /// <summary>Current status of the license.</summary>
    public LicenseStatus Status { get; set; } = LicenseStatus.Active;

    /// <summary>Reference to parent license (for renewals/upgrades).</summary>
    public Guid? ParentLicenseId { get; set; }

    /// <summary>License creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Creator user identifier.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Revocation timestamp.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Revoker user identifier.</summary>
    public string? RevokedBy { get; set; }

    /// <summary>Reason for revocation.</summary>
    public string? RevocationReason { get; set; }

    // Navigation properties
    /// <summary>The customer who owns this license.</summary>
    public Customer? Customer { get; set; }

    /// <summary>The product this license is for.</summary>
    public LicensedProduct? Product { get; set; }

    /// <summary>The parent license (for renewals/upgrades).</summary>
    public License? ParentLicense { get; set; }

    /// <summary>Child licenses (renewals/upgrades/downgrades of this license).</summary>
    public ICollection<License> ChildLicenses { get; set; } = new List<License>();

    /// <summary>Audit events for this license.</summary>
    public ICollection<LicenseEvent> Events { get; set; } = new List<LicenseEvent>();
}
