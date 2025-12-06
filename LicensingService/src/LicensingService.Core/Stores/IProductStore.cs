using LicensingService.Core.Entities;

namespace LicensingService.Core.Stores;

/// <summary>
/// Data access interface for LicensedProduct entities.
/// </summary>
public interface IProductStore
{
    /// <summary>
    /// Gets all products with optional status filter.
    /// </summary>
    Task<IReadOnlyList<LicensedProduct>> GetAllAsync(string? status = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a product by its ID, including option definitions.
    /// </summary>
    Task<LicensedProduct?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a product by its identifier.
    /// </summary>
    Task<LicensedProduct?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new product.
    /// </summary>
    Task<LicensedProduct> CreateAsync(LicensedProduct product, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing product.
    /// </summary>
    Task<LicensedProduct> UpdateAsync(LicensedProduct product, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a product by setting its status to Inactive.
    /// Returns false if product has active licenses.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a product has any licenses.
    /// </summary>
    Task<bool> HasLicensesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an option definition to a product.
    /// </summary>
    Task<ProductOptionDefinition> AddOptionDefinitionAsync(ProductOptionDefinition optionDefinition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an option definition.
    /// </summary>
    Task<ProductOptionDefinition> UpdateOptionDefinitionAsync(ProductOptionDefinition optionDefinition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an option definition. Returns false if option is in use by licenses.
    /// </summary>
    Task<bool> RemoveOptionDefinitionAsync(Guid productId, Guid optionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an option is used by any licenses.
    /// </summary>
    Task<bool> IsOptionInUseAsync(Guid productId, string optionKey, CancellationToken cancellationToken = default);
}
