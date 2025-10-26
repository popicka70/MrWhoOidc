using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Repositories;

internal sealed class LicenseRepository(AuthDbContext dbContext) : ILicenseRepository
{
    public async Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        IQueryable<License> query = dbContext.Licenses
            .AsNoTracking()
            .Where(l => l.IsActive);

        query = tenantId.HasValue
            ? query.Where(l => l.TenantId == tenantId)
            : query.Where(l => l.TenantId == null);

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(license);

        await dbContext.Licenses.AddAsync(license, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return license;
    }

    public async Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(license);

        dbContext.Licenses.Update(license);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return license;
    }

    public async Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default)
    {
        var license = await dbContext.Licenses
            .Where(l => l.IsActive)
            .Where(tenantId.HasValue ? l => l.TenantId == tenantId : l => l.TenantId == null)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (license is null)
        {
            return false;
        }

        license.IsActive = false;
        license.RevokedAt = DateTimeOffset.UtcNow;
        license.RevocationReason = reason;
        license.UpdatedAt = DateTimeOffset.UtcNow;
        license.UpdatedBy = deactivatedBy;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page must be at least 1.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be at least 1.");
        }

        IQueryable<LicenseHistoryEntry> query = dbContext.LicenseHistory.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(actionFilter))
        {
            query = query.Where(h => h.Action == actionFilter);
        }

        if (tenantId.HasValue)
        {
            query = query.Where(h => h.License.TenantId == tenantId);
        }
        else
        {
            query = query.Where(h => h.License.TenantId == null);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(h => h.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<LicenseHistoryEntry>(items, total, page, pageSize);
    }

    public async Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(historyEntry);

        await dbContext.LicenseHistory.AddAsync(historyEntry, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return historyEntry;
    }
}
