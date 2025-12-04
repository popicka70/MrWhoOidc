using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using LicensingService.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LicensingService.Core.Stores;

/// <summary>
/// EF Core implementation of ILicenseStore.
/// </summary>
public class LicenseStore : ILicenseStore
{
    private readonly LicensingDbContext _context;
    private readonly ILicenseTokenGenerator _tokenGenerator;

    public LicenseStore(LicensingDbContext context, ILicenseTokenGenerator tokenGenerator)
    {
        _context = context;
        _tokenGenerator = tokenGenerator;
    }

    public async Task<License> CreateAsync(License license, string createdBy, CancellationToken cancellationToken = default)
    {
        license.Id = GuidHelper.NewId();
        license.CreatedAt = DateTimeOffset.UtcNow;
        license.CreatedBy = createdBy;
        license.Status = LicenseStatus.Active;

        // Parse options from entity
        Dictionary<string, object>? options = null;
        if (!string.IsNullOrEmpty(license.Options))
        {
            options = JsonSerializer.Deserialize<Dictionary<string, object>>(license.Options);
        }

        // Generate the signed JWT token
        var tokenResult = await _tokenGenerator.GenerateAsync(new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = license.Customer?.Identifier ?? throw new InvalidOperationException("Customer must be loaded"),
            ProductIdentifier = license.Product?.Identifier ?? throw new InvalidOperationException("Product must be loaded"),
            Tier = license.Tier,
            Scope = license.Scope,
            ValidFrom = license.ValidFrom,
            ValidUntil = license.ValidUntil,
            Options = options
        }, cancellationToken);

        license.TokenId = tokenResult.TokenId;
        license.SignedToken = tokenResult.Token;
        license.SigningKeyId = tokenResult.Kid;

        _context.Licenses.Add(license);

        // Add created event
        var createdEvent = new LicenseEvent
        {
            Id = GuidHelper.NewId(),
            LicenseId = license.Id,
            EventType = LicenseEventType.Created,
            Actor = createdBy,
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"License created for tier '{license.Tier}'"
        };
        _context.LicenseEvents.Add(createdEvent);

        await _context.SaveChangesAsync(cancellationToken);

        return license;
    }

    public async Task<License?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Product)
            .Include(l => l.ParentLicense)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<License?> GetByTokenIdAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        return await _context.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.TokenId == tokenId, cancellationToken);
    }

    public async Task<IReadOnlyList<License>> GetByCustomerIdAsync(Guid customerId, LicenseStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Licenses
            .Include(l => l.Product)
            .Where(l => l.CustomerId == customerId);

        if (status.HasValue)
        {
            query = query.Where(l => l.Status == status.Value);
        }

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<License>> GetByProductIdAsync(Guid productId, LicenseStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Licenses
            .Include(l => l.Customer)
            .Where(l => l.ProductId == productId);

        if (status.HasValue)
        {
            query = query.Where(l => l.Status == status.Value);
        }

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<License>> GetByCustomerAndProductAsync(Guid customerId, Guid productId, LicenseStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Product)
            .Where(l => l.CustomerId == customerId && l.ProductId == productId);

        if (status.HasValue)
        {
            query = query.Where(l => l.Status == status.Value);
        }

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<License> UpdateAsync(License license, CancellationToken cancellationToken = default)
    {
        _context.Licenses.Update(license);
        await _context.SaveChangesAsync(cancellationToken);
        return license;
    }

    public async Task<License> RevokeAsync(Guid licenseId, string revokedBy, string reason, CancellationToken cancellationToken = default)
    {
        var license = await GetByIdAsync(licenseId, cancellationToken)
            ?? throw new InvalidOperationException($"License {licenseId} not found");

        if (license.Status == LicenseStatus.Revoked)
        {
            throw new InvalidOperationException("License is already revoked");
        }

        license.Status = LicenseStatus.Revoked;
        license.RevokedAt = DateTimeOffset.UtcNow;
        license.RevokedBy = revokedBy;
        license.RevocationReason = reason;

        // Add revocation event
        await AddEventAsync(licenseId, LicenseEventType.Revoked, revokedBy, reason, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return license;
    }

    public async Task<License> RenewAsync(Guid originalLicenseId, DateTimeOffset newValidFrom, DateTimeOffset newValidUntil, string renewedBy, CancellationToken cancellationToken = default)
    {
        var original = await GetByIdAsync(originalLicenseId, cancellationToken)
            ?? throw new InvalidOperationException($"License {originalLicenseId} not found");

        if (original.Status == LicenseStatus.Revoked)
        {
            throw new InvalidOperationException("Cannot renew a revoked license");
        }

        // Create renewal license
        var renewal = new License
        {
            CustomerId = original.CustomerId,
            Customer = original.Customer,
            ProductId = original.ProductId,
            Product = original.Product,
            Tier = original.Tier,
            Scope = original.Scope,
            ValidFrom = newValidFrom,
            ValidUntil = newValidUntil,
            Options = original.Options,
            ParentLicenseId = original.Id
        };

        var createdLicense = await CreateAsync(renewal, renewedBy, cancellationToken);

        // Mark original as renewed
        original.Status = LicenseStatus.Renewed;
        await UpdateAsync(original, cancellationToken);

        // Add renewal event to original
        await AddEventAsync(originalLicenseId, LicenseEventType.Renewed, renewedBy, 
            $"Renewed to license {createdLicense.Id}", cancellationToken);

        return createdLicense;
    }

    public async Task<(IReadOnlyList<License> Items, int TotalCount)> SearchAsync(
        LicenseSearchCriteria criteria,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Product)
            .AsQueryable();

        if (criteria.CustomerId.HasValue)
        {
            query = query.Where(l => l.CustomerId == criteria.CustomerId.Value);
        }

        if (criteria.ProductId.HasValue)
        {
            query = query.Where(l => l.ProductId == criteria.ProductId.Value);
        }

        if (criteria.Status.HasValue)
        {
            query = query.Where(l => l.Status == criteria.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Tier))
        {
            query = query.Where(l => l.Tier == criteria.Tier);
        }

        if (criteria.ValidAt.HasValue)
        {
            var checkTime = criteria.ValidAt.Value;
            query = query.Where(l => 
                l.ValidFrom <= checkTime && 
                l.ValidUntil >= checkTime &&
                l.Status == LicenseStatus.Active);
        }

        if (!string.IsNullOrWhiteSpace(criteria.SearchText))
        {
            var searchLower = criteria.SearchText.ToLowerInvariant();
            query = query.Where(l =>
                (l.Customer != null && (
                    l.Customer.DisplayName.ToLower().Contains(searchLower) ||
                    (l.Customer.ContactEmail != null && l.Customer.ContactEmail.ToLower().Contains(searchLower)))) ||
                (l.Product != null && l.Product.DisplayName.ToLower().Contains(searchLower)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<License>> GetExpiringLicensesAsync(int daysUntilExpiry, CancellationToken cancellationToken = default)
    {
        var threshold = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry);
        var now = DateTimeOffset.UtcNow;

        return await _context.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Product)
            .Where(l => 
                l.Status == LicenseStatus.Active &&
                l.ValidUntil > now &&
                l.ValidUntil <= threshold)
            .OrderBy(l => l.ValidUntil)
            .ToListAsync(cancellationToken);
    }

    public async Task<LicenseEvent> AddEventAsync(Guid licenseId, LicenseEventType eventType, string performedBy, string? details = null, CancellationToken cancellationToken = default)
    {
        var licenseEvent = new LicenseEvent
        {
            Id = GuidHelper.NewId(),
            LicenseId = licenseId,
            EventType = eventType,
            Actor = performedBy,
            Timestamp = DateTimeOffset.UtcNow,
            Details = details
        };

        _context.LicenseEvents.Add(licenseEvent);
        await _context.SaveChangesAsync(cancellationToken);

        return licenseEvent;
    }

    public async Task<IReadOnlyList<LicenseEvent>> GetEventsAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        return await _context.LicenseEvents
            .Where(e => e.LicenseId == licenseId)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
    }
}
