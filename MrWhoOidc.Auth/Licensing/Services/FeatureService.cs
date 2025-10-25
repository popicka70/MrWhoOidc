using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

internal sealed class FeatureService : IFeatureService
{
    private readonly ILicenseService _licenseService;
    private readonly ILogger<FeatureService> _logger;

    public FeatureService(ILicenseService licenseService, ILogger<FeatureService> logger)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        var enabled = await GetEnabledFeaturesAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return enabled.Contains(featureName);
    }

    public async Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var license = await _licenseService.GetCurrentLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (license is null)
        {
            foreach (var feature in FeatureFlags.GetFeaturesForTier(LicenseTier.Community))
            {
                features.Add(feature);
            }
            return features;
        }

        foreach (var feature in FeatureFlags.GetFeaturesForTier(license.TierEnum))
        {
            features.Add(feature);
        }

        foreach (var feature in license.EnabledFeatures)
        {
            features.Add(feature);
        }

        return features;
    }

    public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        // Usage collection will be implemented with analytics work (US4).
        _logger.LogDebug("Feature usage recording deferred until analytics implementation. Feature={Feature} Tenant={Tenant}", featureName, tenantId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        // Analytics service will provide meaningful data in US4; return empty set for now.
        _logger.LogDebug("Feature usage retrieval deferred until analytics implementation. Tenant={Tenant} Feature={Feature}", tenantId, featureName);
        return Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
    }
}
