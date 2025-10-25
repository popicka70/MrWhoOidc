using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

internal sealed class LimitService : ILimitService
{
    private static readonly IReadOnlyDictionary<LicenseTier, IReadOnlyDictionary<string, long>> DefaultLimits =
        new Dictionary<LicenseTier, IReadOnlyDictionary<string, long>>
        {
            [LicenseTier.Community] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [LicenseLimitTypes.Users] = 100,
                [LicenseLimitTypes.Tenants] = 1,
                [LicenseLimitTypes.Clients] = 25
            },
            [LicenseTier.Professional] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [LicenseLimitTypes.Users] = 10_000,
                [LicenseLimitTypes.Tenants] = 5,
                [LicenseLimitTypes.Clients] = 250
            },
            [LicenseTier.Enterprise] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [LicenseLimitTypes.Users] = -1,
                [LicenseLimitTypes.Tenants] = -1,
                [LicenseLimitTypes.Clients] = -1
            },
            [LicenseTier.EnterprisePlus] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [LicenseLimitTypes.Users] = -1,
                [LicenseLimitTypes.Tenants] = -1,
                [LicenseLimitTypes.Clients] = -1
            }
        };

    private readonly ILicenseService _licenseService;
    private readonly ILogger<LimitService> _logger;

    public LimitService(ILicenseService licenseService, ILogger<LimitService> logger)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<long> GetLimitAsync(string limitType, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitType);

        var license = await _licenseService.GetCurrentLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return ResolveLimitValue(license, limitType);
    }

    public async Task<bool> IsWithinLimitAsync(string limitType, long currentUsage, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var limit = await GetLimitAsync(limitType, tenantId, cancellationToken).ConfigureAwait(false);
        if (limit < 0)
        {
            return true;
        }

        return currentUsage <= limit;
    }

    public async Task<IReadOnlyList<UsageLimitInfo>> GetUsageLimitsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var license = await _licenseService.GetCurrentLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var tier = license?.TierEnum ?? LicenseTier.Community;
        var limits = MergeLimits(license, tier);

        var list = new List<UsageLimitInfo>(limits.Count);
        foreach (var (limitType, limitValue) in limits)
        {
            // Current usage data will be integrated during analytics work (US4). For now, surface zero usage.
            const long currentUsage = 0;
            var utilization = limitValue <= 0 ? 0d : Math.Clamp((double)currentUsage / limitValue, 0d, 1d);
            var info = new UsageLimitInfo(
                limitType,
                currentUsage,
                limitValue,
                utilization,
                IsNearLimit(limitValue, currentUsage),
                IsAtLimit(limitValue, currentUsage));
            list.Add(info);
        }

        return list;
    }

    public async Task<bool> CanAddAsync(string limitType, long currentUsage, int additionalCount = 1, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (additionalCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalCount), additionalCount, "Additional count cannot be negative.");
        }

        var limit = await GetLimitAsync(limitType, tenantId, cancellationToken).ConfigureAwait(false);
        if (limit < 0)
        {
            return true;
        }

        var projected = currentUsage + additionalCount;
        return projected <= limit;
    }

    private static long ResolveLimitValue(LicenseInfo? license, string limitType)
    {
        if (license is null)
        {
            return GetDefaultLimit(LicenseTier.Community, limitType);
        }

        if (license.Limits.TryGetValue(limitType, out var value))
        {
            return value;
        }

        return GetDefaultLimit(license.TierEnum, limitType);
    }

    private static IReadOnlyDictionary<string, long> MergeLimits(LicenseInfo? license, LicenseTier tier)
    {
        var defaults = DefaultLimits.TryGetValue(tier, out var table)
            ? table
            : DefaultLimits[LicenseTier.Community];

        if (license is null)
        {
            return defaults;
        }

        if (license.Limits.Count == 0)
        {
            return defaults;
        }

        var merged = new Dictionary<string, long>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in license.Limits)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static long GetDefaultLimit(LicenseTier tier, string limitType)
    {
        if (DefaultLimits.TryGetValue(tier, out var limits) && limits.TryGetValue(limitType, out var value))
        {
            return value;
        }

        if (DefaultLimits[LicenseTier.Community].TryGetValue(limitType, out var fallback))
        {
            return fallback;
        }

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
}
