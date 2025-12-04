using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensingService.Core.Stores;

/// <summary>
/// EF Core implementation of IProductStore.
/// </summary>
public class ProductStore : IProductStore
{
    private readonly LicensingDbContext _dbContext;

    public ProductStore(LicensingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<LicensedProduct>> GetAllAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Products.AsQueryable();

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(p => p.Status == status);
        }

        return await query
            .OrderBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<LicensedProduct?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Products
            .Include(p => p.OptionDefinitions.OrderBy(o => o.SortOrder))
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<LicensedProduct?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Products
            .Include(p => p.OptionDefinitions.OrderBy(o => o.SortOrder))
            .FirstOrDefaultAsync(p => p.Identifier == identifier, cancellationToken);
    }

    public async Task<LicensedProduct> CreateAsync(LicensedProduct product, CancellationToken cancellationToken = default)
    {
        product.Id = GuidHelper.NewId();
        product.CreatedAt = DateTimeOffset.UtcNow;
        product.Status = "Active";

        _dbContext.Products.Add(product);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return product;
    }

    public async Task<LicensedProduct> UpdateAsync(LicensedProduct product, CancellationToken cancellationToken = default)
    {
        product.UpdatedAt = DateTimeOffset.UtcNow;
        _dbContext.Products.Update(product);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return product;
    }

    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Check for active licenses
        if (await HasLicensesAsync(id, cancellationToken))
        {
            return false;
        }

        var product = await _dbContext.Products.FindAsync([id], cancellationToken);
        if (product == null)
        {
            return false;
        }

        product.Status = "Inactive";
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> HasLicensesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Licenses
            .AnyAsync(l => l.ProductId == id, cancellationToken);
    }

    public async Task<ProductOptionDefinition> AddOptionDefinitionAsync(ProductOptionDefinition optionDefinition, CancellationToken cancellationToken = default)
    {
        optionDefinition.Id = GuidHelper.NewId();

        _dbContext.ProductOptionDefinitions.Add(optionDefinition);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return optionDefinition;
    }

    public async Task<ProductOptionDefinition> UpdateOptionDefinitionAsync(ProductOptionDefinition optionDefinition, CancellationToken cancellationToken = default)
    {
        _dbContext.ProductOptionDefinitions.Update(optionDefinition);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return optionDefinition;
    }

    public async Task<bool> RemoveOptionDefinitionAsync(Guid productId, Guid optionId, CancellationToken cancellationToken = default)
    {
        var optionDef = await _dbContext.ProductOptionDefinitions
            .FirstOrDefaultAsync(o => o.Id == optionId && o.ProductId == productId, cancellationToken);

        if (optionDef == null)
        {
            return false;
        }

        // Check if option is in use by any licenses
        if (await IsOptionInUseAsync(productId, optionDef.OptionKey, cancellationToken))
        {
            return false;
        }

        _dbContext.ProductOptionDefinitions.Remove(optionDef);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> IsOptionInUseAsync(Guid productId, string optionKey, CancellationToken cancellationToken = default)
    {
        // Check if any license for this product uses this option key in its JSON options
        var licenses = await _dbContext.Licenses
            .Where(l => l.ProductId == productId && l.Options != null)
            .Select(l => l.Options)
            .ToListAsync(cancellationToken);

        foreach (var optionsJson in licenses)
        {
            if (string.IsNullOrEmpty(optionsJson))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(optionsJson);
                if (doc.RootElement.TryGetProperty(optionKey, out _))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore invalid JSON
            }
        }

        return false;
    }
}
