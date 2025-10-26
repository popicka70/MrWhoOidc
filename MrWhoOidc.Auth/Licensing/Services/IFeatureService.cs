using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Licensing.Services;

public interface IFeatureService
{
    Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);
}
