using LicensingService.Core.Entities;

namespace LicensingService.Core.Stores;

/// <summary>
/// Data access interface for Customer entities.
/// </summary>
public interface ICustomerStore
{
    /// <summary>
    /// Gets all customers with optional filters and pagination.
    /// </summary>
    Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetAllAsync(
        string? search = null,
        string? status = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a customer by its ID.
    /// </summary>
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a customer by its identifier.
    /// </summary>
    Task<Customer?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new customer.
    /// </summary>
    Task<Customer> CreateAsync(Customer customer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing customer.
    /// </summary>
    Task<Customer> UpdateAsync(Customer customer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a customer by setting its status to Inactive.
    /// Returns false if customer has active licenses.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a customer has any licenses.
    /// </summary>
    Task<bool> HasLicensesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the license count for a customer.
    /// </summary>
    Task<int> GetLicenseCountAsync(Guid id, CancellationToken cancellationToken = default);
}
