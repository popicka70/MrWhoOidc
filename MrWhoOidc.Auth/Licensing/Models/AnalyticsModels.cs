using System;
using System.Collections.Generic;

namespace MrWhoOidc.Auth.Licensing.Models;

/// <summary>
/// Aggregated usage information for a specific licensed feature.
/// </summary>
public sealed record FeatureUsageSummary(
    string FeatureName,
    long UsageCount,
    DateTimeOffset FirstUsed,
    DateTimeOffset LastUsed);

/// <summary>
/// Report describing feature usage within a time window.
/// </summary>
public sealed record FeatureUsageReport(
    IReadOnlyList<FeatureUsageSummary> Metrics,
    string AggregationPeriod,
    DateTimeOffset FromDate,
    DateTimeOffset ToDate);

/// <summary>
/// Report pairing license information with current usage versus configured limits.
/// </summary>
public sealed record UsageLimitsReport(
    LicenseInfo License,
    IReadOnlyList<UsageLimitInfo> Limits);

/// <summary>
/// Pricing descriptor for a tier. Optional in current implementation.
/// </summary>
public sealed record LicenseTierPricing(
    string Currency,
    decimal Amount,
    string Period);

/// <summary>
/// Describes a license tier, its features, and default limits.
/// </summary>
public sealed record LicenseTierDescriptor(
    LicenseTier Tier,
    string TierKey,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long> DefaultLimits,
    LicenseTierPricing? Pricing);
