namespace LicensingService.Core.Entities;

/// <summary>
/// Defines an available licensable option for a product.
/// </summary>
public class ProductOptionDefinition
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>Reference to the parent product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Option identifier (e.g., "max_users"). Must be lowercase alphanumeric with underscores.</summary>
    public string OptionKey { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Data type of the option value.</summary>
    public OptionDataType DataType { get; set; }

    /// <summary>Default value (stored as string).</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Help text for administrators.</summary>
    public string? Description { get; set; }

    /// <summary>Display order in UI.</summary>
    public int SortOrder { get; set; }

    // Navigation properties
    /// <summary>The product this option definition belongs to.</summary>
    public LicensedProduct? Product { get; set; }
}
