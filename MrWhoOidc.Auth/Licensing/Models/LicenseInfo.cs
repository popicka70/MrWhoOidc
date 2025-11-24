using System;
using System.Collections.Generic;

namespace MrWhoOidc.Auth.Licensing.Models;

public sealed record LicenseInfo(
    string Tier,
    string? OrganizationName,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    IReadOnlySet<string> EnabledFeatures,
    IReadOnlyDictionary<string, long> Limits,
    bool IsExpired,
    bool IsValid,
    LicenseScope Scope,
    string? IssuedTo,
    Guid? LicensedTenantId,
    string? LicensedTenantSlug,
    IReadOnlySet<string> DefaultTenantFeatures,
    bool HasExplicitScopeClaim,
    IReadOnlySet<string> AllowedIssuers)
{
    public LicenseTier TierEnum => LicenseTierExtensions.FromTierString(Tier);

    public bool IsFeatureEnabled(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return EnabledFeatures.Contains(featureName);
    }

    public long GetLimit(string limitType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitType);
        return Limits.TryGetValue(limitType, out var value) ? value : 0L;
    }

    public bool HasUnlimitedAccess(string limitType) => GetLimit(limitType) == -1;

    public TimeSpan TimeUntilExpiry => ValidUntil - DateTimeOffset.UtcNow;

    public bool IsNearExpiry(TimeSpan threshold)
    {
        if (threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be non-negative.");
        }

        var remaining = TimeUntilExpiry;
        return remaining <= threshold && remaining > TimeSpan.Zero;
    }
}
