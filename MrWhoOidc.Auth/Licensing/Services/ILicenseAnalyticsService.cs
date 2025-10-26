using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

public interface ILicenseAnalyticsService
{
    Task<FeatureUsageReport> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    Task<UsageLimitsReport> GetUsageLimitsAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LicenseTierDescriptor>> GetLicenseTiersAsync(CancellationToken cancellationToken = default);
}
