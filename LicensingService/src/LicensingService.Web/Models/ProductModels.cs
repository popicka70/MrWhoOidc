using System.ComponentModel.DataAnnotations;
using LicensingService.Core.Entities;

namespace LicensingService.Web.Models;

/// <summary>
/// Request to create a new licensed product.
/// </summary>
public class CreateProductRequest
{
    /// <summary>Product code (e.g., "mrwho-oidc"). Must be lowercase alphanumeric with hyphens.</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Identifier must be lowercase alphanumeric with hyphens")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Product description.</summary>
    [StringLength(1000)]
    public string? Description { get; set; }
}

/// <summary>
/// Request to update a licensed product.
/// </summary>
public class UpdateProductRequest
{
    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Product description.</summary>
    [StringLength(1000)]
    public string? Description { get; set; }
}

/// <summary>
/// Response containing a product's details.
/// </summary>
public class ProductResponse
{
    public Guid Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static ProductResponse FromEntity(LicensedProduct product) => new()
    {
        Id = product.Id,
        Identifier = product.Identifier,
        DisplayName = product.DisplayName,
        Description = product.Description,
        Status = product.Status,
        CreatedAt = product.CreatedAt,
        UpdatedAt = product.UpdatedAt
    };
}

/// <summary>
/// Response containing a product's details with option definitions.
/// </summary>
public class ProductWithOptionsResponse : ProductResponse
{
    public List<OptionDefinitionResponse> OptionDefinitions { get; set; } = [];

    public static new ProductWithOptionsResponse FromEntity(LicensedProduct product) => new()
    {
        Id = product.Id,
        Identifier = product.Identifier,
        DisplayName = product.DisplayName,
        Description = product.Description,
        Status = product.Status,
        CreatedAt = product.CreatedAt,
        UpdatedAt = product.UpdatedAt,
        OptionDefinitions = product.OptionDefinitions
            .Select(OptionDefinitionResponse.FromEntity)
            .ToList()
    };
}

/// <summary>
/// Paginated list of products.
/// </summary>
public class ProductListResponse
{
    public List<ProductResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
}
