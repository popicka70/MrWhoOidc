using LicensingService.Core.Entities;

namespace LicensingService.Core.Stores;

/// <summary>
/// Store for managing licenses.
/// </summary>
public interface ILicenseStore
{
    /// <summary>
    /// Creates a new license.
    /// </summary>
    Task<License> CreateAsync(License license, string createdBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a license by ID.
    /// </summary>
    Task<License?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a license by token ID (jti).
    /// </summary>
    Task<License?> GetByTokenIdAsync(string tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets licenses for a customer with optional status filter.
    /// </summary>
    Task<IReadOnlyList<License>> GetByCustomerIdAsync(Guid customerId, LicenseStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets licenses for a product with optional status filter.
    /// </summary>
    Task<IReadOnlyList<License>> GetByProductIdAsync(Guid productId, LicenseStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets licenses for a customer and product.
    /// </summary>
    Task<IReadOnlyList<License>> GetByCustomerAndProductAsync(Guid customerId, Guid productId, LicenseStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a license.
    /// </summary>
    Task<License> UpdateAsync(License license, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a license.
    /// </summary>
    Task<License> RevokeAsync(Guid licenseId, string revokedBy, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a license by creating a new license with overlap.
    /// </summary>
    Task<License> RenewAsync(Guid originalLicenseId, DateTimeOffset newValidFrom, DateTimeOffset newValidUntil, string renewedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches licenses with various filters.
    /// </summary>
    Task<(IReadOnlyList<License> Items, int TotalCount)> SearchAsync(
        LicenseSearchCriteria criteria,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets licenses that are expiring within a specified period.
    /// </summary>
    Task<IReadOnlyList<License>> GetExpiringLicensesAsync(int daysUntilExpiry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an event to a license's event log.
    /// </summary>
    Task<LicenseEvent> AddEventAsync(Guid licenseId, LicenseEventType eventType, string performedBy, string? details = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets events for a license.
    /// </summary>
    Task<IReadOnlyList<LicenseEvent>> GetEventsAsync(Guid licenseId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Criteria for searching licenses.
/// </summary>
public class LicenseSearchCriteria
{
    /// <summary>Filter by customer ID.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>Filter by product ID.</summary>
    public Guid? ProductId { get; init; }

    /// <summary>Filter by status.</summary>
    public LicenseStatus? Status { get; init; }

    /// <summary>Filter by tier (exact match).</summary>
    public string? Tier { get; init; }

    /// <summary>Filter licenses valid at a specific point in time.</summary>
    public DateTimeOffset? ValidAt { get; init; }

    /// <summary>Text search across customer name, email, or product name.</summary>
    public string? SearchText { get; init; }
}
