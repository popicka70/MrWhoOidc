using System.ComponentModel.DataAnnotations;
using LicensingService.Core.Entities;

namespace LicensingService.Web.Models;

/// <summary>
/// Request to create a new option definition.
/// </summary>
public class CreateOptionDefinitionRequest
{
    /// <summary>Option key (e.g., "max_users"). Must be lowercase alphanumeric with underscores.</summary>
    [Required]
    [StringLength(50, MinimumLength = 1)]
    [RegularExpression(@"^[a-z0-9_]+$", ErrorMessage = "OptionKey must be lowercase alphanumeric with underscores")]
    public string OptionKey { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Data type of the option value.</summary>
    [Required]
    public OptionDataType DataType { get; set; }

    /// <summary>Default value (as string).</summary>
    [StringLength(200)]
    public string? DefaultValue { get; set; }

    /// <summary>Help text for administrators.</summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Display order in UI.</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Request to update an option definition.
/// </summary>
public class UpdateOptionDefinitionRequest
{
    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Default value (as string).</summary>
    [StringLength(200)]
    public string? DefaultValue { get; set; }

    /// <summary>Help text for administrators.</summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Display order in UI.</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Response containing an option definition.
/// </summary>
public class OptionDefinitionResponse
{
    public Guid Id { get; set; }
    public string OptionKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }

    public static OptionDefinitionResponse FromEntity(ProductOptionDefinition entity) => new()
    {
        Id = entity.Id,
        OptionKey = entity.OptionKey,
        DisplayName = entity.DisplayName,
        DataType = entity.DataType.ToString(),
        DefaultValue = entity.DefaultValue,
        Description = entity.Description,
        SortOrder = entity.SortOrder
    };
}
