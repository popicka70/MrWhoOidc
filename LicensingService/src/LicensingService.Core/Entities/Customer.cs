namespace LicensingService.Core.Entities;

/// <summary>
/// Represents a licensed customer or organization.
/// </summary>
public class Customer
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>Business identifier (e.g., "ACME-001"). Must be alphanumeric with hyphens.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Primary contact email.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Primary contact person.</summary>
    public string? ContactName { get; set; }

    /// <summary>Status: Active or Inactive.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Record creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last modification timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    // Navigation properties
    /// <summary>Licenses owned by this customer.</summary>
    public ICollection<License> Licenses { get; set; } = new List<License>();
}
