using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Repositories;

internal sealed class FeatureUsageRepository : IFeatureUsageRepository
{
    private readonly AuthDbContext _db;

    public FeatureUsageRepository(AuthDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task RecordUsageAsync(
        string featureName,
        Guid? tenantId,
        Guid? licenseId,
        DateTimeOffset occurredAt,
        long increment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        if (increment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), increment, "Increment must be positive.");
        }

        var date = DateOnly.FromDateTime(occurredAt.UtcDateTime);

        var metric = await _db.FeatureUsageMetrics
            .FirstOrDefaultAsync(
                x => x.FeatureName == featureName &&
                     x.AggregationDate == date &&
                     x.TenantId == tenantId,
                cancellationToken)
            .ConfigureAwait(false);

        if (metric is null)
        {
            metric = new FeatureUsageMetric
            {
                FeatureName = featureName,
                TenantId = tenantId,
                LicenseId = licenseId,
                UsageCount = increment,
                FirstUsed = occurredAt,
                LastUsed = occurredAt,
                AggregationDate = date
            };

            _db.FeatureUsageMetrics.Add(metric);
        }
        else
        {
            metric.UsageCount += increment;
            if (occurredAt < metric.FirstUsed)
            {
                metric.FirstUsed = occurredAt;
            }
            if (occurredAt > metric.LastUsed)
            {
                metric.LastUsed = occurredAt;
            }

            if (licenseId.HasValue)
            {
                metric.LicenseId = licenseId;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeatureUsageMetric>> GetUsageAsync(
        Guid? tenantId,
        string? featureName,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        if (fromDate > toDate)
        {
            throw new ArgumentException("fromDate must be earlier than or equal to toDate", nameof(fromDate));
        }

        var query = _db.FeatureUsageMetrics.AsNoTracking().AsQueryable();

        if (tenantId.HasValue)
        {
            var id = tenantId.Value;
            query = query.Where(x => x.TenantId == id);
        }

        if (!string.IsNullOrWhiteSpace(featureName))
        {
            query = query.Where(x => x.FeatureName == featureName);
        }

        query = query
            .Where(x => x.AggregationDate >= fromDate && x.AggregationDate <= toDate)
            .OrderBy(x => x.FeatureName)
            .ThenBy(x => x.AggregationDate);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
