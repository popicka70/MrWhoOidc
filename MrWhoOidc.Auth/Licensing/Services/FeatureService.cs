using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Repositories;

namespace MrWhoOidc.Auth.Licensing.Services;

internal sealed class FeatureService : IFeatureService
{
    private readonly ILicenseService _licenseService;
    private readonly ILicenseRepository _licenseRepository;
    private readonly IFeatureUsageRepository _usageRepository;
    private readonly ILogger<FeatureService> _logger;
    private readonly TimeProvider _timeProvider;

    public FeatureService(
        ILicenseService licenseService,
        ILicenseRepository licenseRepository,
        IFeatureUsageRepository usageRepository,
        ILogger<FeatureService> logger,
        TimeProvider? timeProvider = null)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _licenseRepository = licenseRepository ?? throw new ArgumentNullException(nameof(licenseRepository));
        _usageRepository = usageRepository ?? throw new ArgumentNullException(nameof(usageRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        var enabled = await GetEnabledFeaturesAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return enabled.Contains(featureName);
    }

    public async Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var license = await _licenseService.GetEffectiveLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);

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

    public async Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        Guid? licenseId = null;
        try
        {
            var license = await _licenseRepository.GetActiveLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
            licenseId = license?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve license id while recording feature usage for {Feature} (tenant {Tenant}).", featureName, tenantId);
        }

        try
        {
            await _usageRepository.RecordUsageAsync(
                featureName,
                tenantId,
                licenseId,
                _timeProvider.GetUtcNow(),
                1,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record feature usage for {Feature} (tenant {Tenant}).", featureName, tenantId);
        }
    }

    public async Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var to = toDate ?? now;
        var from = fromDate ?? to.AddDays(-30);

        if (from > to)
        {
            throw new ArgumentException("fromDate must be earlier than or equal to toDate.", nameof(fromDate));
        }

        try
        {
            var fromOnly = DateOnly.FromDateTime(from.UtcDateTime);
            var toOnly = DateOnly.FromDateTime(to.UtcDateTime);
            return await _usageRepository
                .GetUsageAsync(tenantId, featureName, fromOnly, toOnly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load feature usage for {Feature} (tenant {Tenant}).", featureName ?? "all", tenantId);
            return Array.Empty<FeatureUsageMetric>();
        }
    }
}
