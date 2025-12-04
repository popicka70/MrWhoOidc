namespace LicensingService.Core.Entities;

/// <summary>
/// Represents a product/service that can be licensed.
/// </summary>
public class LicensedProduct
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>Product code (e.g., "mrwho-oidc"). Must be lowercase alphanumeric with hyphens.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Product description.</summary>
    public string? Description { get; set; }

    /// <summary>Status: Active or Inactive.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Record creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last modification timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    // Navigation properties
    /// <summary>Available licensable options for this product.</summary>
    public ICollection<ProductOptionDefinition> OptionDefinitions { get; set; } = new List<ProductOptionDefinition>();

    /// <summary>Licenses issued for this product.</summary>
    public ICollection<License> Licenses { get; set; } = new List<License>();
}
