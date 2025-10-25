using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Services;

internal sealed class LicenseAnalyticsService : ILicenseAnalyticsService
{
    private static readonly IReadOnlyDictionary<LicenseTier, string> TierDescriptions = new Dictionary<LicenseTier, string>
    {
        [LicenseTier.Community] = "Core OIDC flows for single-tenant deployments.",
        [LicenseTier.Professional] = "Adds multi-tenancy and advanced security enhancements.",
        [LicenseTier.Enterprise] = "Scales without limits and unlocks enterprise integrations.",
        [LicenseTier.EnterprisePlus] = "Premium capabilities and roadmap features for large enterprises."
    };

    private readonly AuthDbContext _db;
    private readonly IFeatureUsageRepository _usageRepository;
    private readonly ILicenseService _licenseService;
    private readonly ILimitService _limitService;
    private readonly ILogger<LicenseAnalyticsService> _logger;
    private readonly TimeProvider _timeProvider;

    public LicenseAnalyticsService(
        AuthDbContext db,
        IFeatureUsageRepository usageRepository,
        ILicenseService licenseService,
        ILimitService limitService,
        ILogger<LicenseAnalyticsService> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _usageRepository = usageRepository ?? throw new ArgumentNullException(nameof(usageRepository));
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _limitService = limitService ?? throw new ArgumentNullException(nameof(limitService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FeatureUsageReport> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        var utcNow = _timeProvider.GetUtcNow();
        var toValue = to ?? utcNow;
        var fromValue = from ?? toValue.AddDays(-30);

        if (fromValue > toValue)
        {
            throw new ArgumentException("The 'from' date must be earlier than or equal to the 'to' date.", nameof(from));
        }

        var fromDateOnly = DateOnly.FromDateTime(fromValue.UtcDateTime);
        var toDateOnly = DateOnly.FromDateTime(toValue.UtcDateTime);

        var metrics = await _usageRepository
            .GetUsageAsync(tenantId, featureName, fromDateOnly, toDateOnly, cancellationToken)
            .ConfigureAwait(false);

        var grouped = metrics
            .GroupBy(m => m.FeatureName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FeatureUsageSummary(
                g.Key,
                g.Sum(x => x.UsageCount),
                g.Min(x => x.FirstUsed),
                g.Max(x => x.LastUsed)))
            .OrderByDescending(x => x.UsageCount)
            .ThenBy(x => x.FeatureName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FeatureUsageReport(grouped, "daily", fromValue, toValue);
    }

    public async Task<UsageLimitsReport> GetUsageLimitsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var license = await _licenseService.GetCurrentLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (license is null)
        {
            throw new InvalidOperationException("No license information available for the requested scope.");
        }

        var tier = license.TierEnum;
        var limitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in LicenseDefaultLimits.GetDefaults(tier))
        {
            limitKeys.Add(kvp.Key);
        }
        foreach (var kvp in license.Limits)
        {
            limitKeys.Add(kvp.Key);
        }

        var results = new List<UsageLimitInfo>(limitKeys.Count);
        foreach (var key in limitKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var limitValue = await _limitService.GetLimitAsync(key, tenantId, cancellationToken).ConfigureAwait(false);
            var currentUsage = await GetCurrentUsageAsync(key, tenantId, cancellationToken).ConfigureAwait(false);

            double utilization = 0d;
            if (limitValue > 0)
            {
                utilization = Math.Clamp((double)currentUsage / limitValue, 0d, 1d);
            }

            var info = new UsageLimitInfo(
                key,
                currentUsage,
                limitValue,
                utilization,
                IsNearLimit(limitValue, currentUsage),
                IsAtLimit(limitValue, currentUsage));
            results.Add(info);
        }

        return new UsageLimitsReport(license, results);
    }

    public Task<IReadOnlyList<LicenseTierDescriptor>> GetLicenseTiersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LicenseTierDescriptor> data = new List<LicenseTierDescriptor>
        {
            CreateDescriptor(LicenseTier.Community),
            CreateDescriptor(LicenseTier.Professional),
            CreateDescriptor(LicenseTier.Enterprise),
            CreateDescriptor(LicenseTier.EnterprisePlus)
        };

        return Task.FromResult(data);
    }

    private async Task<long> GetCurrentUsageAsync(string limitType, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (string.Equals(limitType, LicenseLimitTypes.Users, StringComparison.OrdinalIgnoreCase))
        {
            return tenantId.HasValue
                ? await _db.Users.LongCountAsync(u => u.TenantId == tenantId.Value, cancellationToken).ConfigureAwait(false)
                : await _db.Users.LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(limitType, LicenseLimitTypes.Tenants, StringComparison.OrdinalIgnoreCase))
        {
            return tenantId.HasValue ? 1 : await _db.Tenants.LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(limitType, LicenseLimitTypes.Clients, StringComparison.OrdinalIgnoreCase))
        {
            return tenantId.HasValue
                ? await _db.Clients.LongCountAsync(c => c.TenantId == tenantId.Value, cancellationToken).ConfigureAwait(false)
                : await _db.Clients.LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("No usage calculator implemented for limit type '{LimitType}'. Defaulting to zero usage.", limitType);
        return 0;
    }

    private static bool IsNearLimit(long limit, long current)
    {
        if (limit <= 0)
        {
            return false;
        }

        var ratio = (double)current / limit;
        return ratio >= 0.8d && ratio < 1d;
    }

    private static bool IsAtLimit(long limit, long current)
    {
        if (limit < 0)
        {
            return false;
        }

        return current >= limit;
    }

    private static LicenseTierDescriptor CreateDescriptor(LicenseTier tier)
    {
        var features = FeatureFlags.GetFeaturesForTier(tier).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var limits = new Dictionary<string, long>(LicenseDefaultLimits.GetDefaults(tier), StringComparer.OrdinalIgnoreCase);
        var key = tier.ToTierString();
        var display = LicensePageFriendlyName(key);
        var description = TierDescriptions.TryGetValue(tier, out var desc) ? desc : string.Empty;
        return new LicenseTierDescriptor(tier, key, display, description, features, limits, null);
    }

    private static string LicensePageFriendlyName(string tierKey)
    {
        return tierKey switch
        {
            "community" => "Community",
            "professional" => "Professional",
            "enterprise" => "Enterprise",
            "enterprise+" => "Enterprise+",
            _ => tierKey
        };
    }
}
