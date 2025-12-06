using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensingService.Core.Stores;

/// <summary>
/// EF Core implementation of ICustomerStore.
/// </summary>
public class CustomerStore : ICustomerStore
{
    private readonly LicensingDbContext _dbContext;

    public CustomerStore(LicensingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetAllAsync(
        string? search = null,
        string? status = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Customers.AsQueryable();

        // Filter by status
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(c => c.Status == status);
        }

        // Search by name or identifier (prefix match)
        if (!string.IsNullOrEmpty(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(c =>
                c.DisplayName.ToLower().StartsWith(searchLower) ||
                c.Identifier.ToLower().StartsWith(searchLower));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(c => c.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Customer?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Customers
            .FirstOrDefaultAsync(c => c.Identifier == identifier, cancellationToken);
    }

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        customer.Id = GuidHelper.NewId();
        customer.CreatedAt = DateTimeOffset.UtcNow;
        customer.Status = "Active";

        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return customer;
    }

    public async Task<Customer> UpdateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        _dbContext.Customers.Update(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return customer;
    }

    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Check for active licenses
        var hasActiveLicenses = await _dbContext.Licenses
            .AnyAsync(l => l.CustomerId == id && l.Status == LicenseStatus.Active, cancellationToken);

        if (hasActiveLicenses)
        {
            return false;
        }

        var customer = await _dbContext.Customers.FindAsync([id], cancellationToken);
        if (customer == null)
        {
            return false;
        }

        customer.Status = "Inactive";
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> HasLicensesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Licenses
            .AnyAsync(l => l.CustomerId == id, cancellationToken);
    }

    public async Task<int> GetLicenseCountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Licenses
            .CountAsync(l => l.CustomerId == id, cancellationToken);
    }
}
