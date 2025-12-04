using System.ComponentModel.DataAnnotations;
using LicensingService.Core.Entities;

namespace LicensingService.Web.Models;

/// <summary>
/// Request to create a new customer.
/// </summary>
public class CreateCustomerRequest
{
    /// <summary>Business identifier (e.g., "ACME-001"). Must be alphanumeric with hyphens.</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression(@"^[A-Za-z0-9-]+$", ErrorMessage = "Identifier must be alphanumeric with hyphens")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Primary contact email.</summary>
    [StringLength(254)]
    [EmailAddress]
    public string? ContactEmail { get; set; }

    /// <summary>Primary contact person.</summary>
    [StringLength(200)]
    public string? ContactName { get; set; }
}

/// <summary>
/// Request to update a customer.
/// </summary>
public class UpdateCustomerRequest
{
    /// <summary>Human-readable name.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Primary contact email.</summary>
    [StringLength(254)]
    [EmailAddress]
    public string? ContactEmail { get; set; }

    /// <summary>Primary contact person.</summary>
    [StringLength(200)]
    public string? ContactName { get; set; }
}

/// <summary>
/// Response containing a customer's details.
/// </summary>
public class CustomerResponse
{
    public Guid Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactName { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static CustomerResponse FromEntity(Customer customer) => new()
    {
        Id = customer.Id,
        Identifier = customer.Identifier,
        DisplayName = customer.DisplayName,
        ContactEmail = customer.ContactEmail,
        ContactName = customer.ContactName,
        Status = customer.Status,
        CreatedAt = customer.CreatedAt,
        UpdatedAt = customer.UpdatedAt
    };
}

/// <summary>
/// Customer response with license count.
/// </summary>
public class CustomerWithLicenseCountResponse : CustomerResponse
{
    public int LicenseCount { get; set; }
}

/// <summary>
/// Paginated list of customers.
/// </summary>
public class CustomerListResponse
{
    public List<CustomerResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
