using System;
using System.Collections.Generic;

namespace MrWhoOidc.Auth.Licensing.Models;

/// <summary>
/// Provides access to the built-in per-tier limit defaults used across licensing services.
/// </summary>
public static class LicenseDefaultLimits
{
    private static readonly IReadOnlyDictionary<string, long> CommunityLimits =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [LicenseLimitTypes.Users] = 100,
            [LicenseLimitTypes.Tenants] = 1,
            [LicenseLimitTypes.Clients] = 25
        };

    private static readonly IReadOnlyDictionary<string, long> ProfessionalLimits =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [LicenseLimitTypes.Users] = 10_000,
            [LicenseLimitTypes.Tenants] = 5,
            [LicenseLimitTypes.Clients] = 250
        };

    private static readonly IReadOnlyDictionary<string, long> EnterpriseLimits =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [LicenseLimitTypes.Users] = -1,
            [LicenseLimitTypes.Tenants] = -1,
            [LicenseLimitTypes.Clients] = -1
        };

    private static readonly IReadOnlyDictionary<string, long> EnterprisePlusLimits = EnterpriseLimits;

    /// <summary>
    /// Returns the default limit table for the supplied tier.
    /// </summary>
    public static IReadOnlyDictionary<string, long> GetDefaults(LicenseTier tier) => tier switch
    {
        LicenseTier.Community => CommunityLimits,
        LicenseTier.Professional => ProfessionalLimits,
        LicenseTier.Enterprise => EnterpriseLimits,
        LicenseTier.EnterprisePlus => EnterprisePlusLimits,
        _ => CommunityLimits
    };
}
