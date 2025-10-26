using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Licensing.Repositories;

public interface IFeatureUsageRepository
{
    Task RecordUsageAsync(
        string featureName,
        Guid? tenantId,
        Guid? licenseId,
        DateTimeOffset occurredAt,
        long increment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureUsageMetric>> GetUsageAsync(
        Guid? tenantId,
        string? featureName,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);
}
