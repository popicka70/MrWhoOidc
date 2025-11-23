using System;
using System.Collections.Generic;
using System.Linq;

namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Provides the canonical feature set for each supported license tier so the KeyGen UI
/// can surface built-in capabilities consistently with the auth service.
/// </summary>
public static class LicenseTierCatalog
{
    private static readonly string[] CommunityFeatures =
    {
        "basic_oidc",
        "basic_admin_ui"
    };

    private static readonly string[] ProfessionalOnlyFeatures =
    {
        "multi_tenancy",
        "advanced_security",
        "client_secret_rotation",
        "enhanced_audit_logging"
    };

    private static readonly string[] EnterpriseOnlyFeatures =
    {
        "unlimited_scale",
        "dpop",
        "token_exchange",
        "backchannel_logout",
        "ldap_integration",
        "custom_claim_mappings",
        "advanced_monitoring"
    };

    private static readonly string[] EnterprisePlusOnlyFeatures =
    {
        "webauthn",
        "risk_based_auth",
        "hsm_integration",
        "professional_services"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TierFeatureMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["community"] = CommunityFeatures,
            ["professional"] = CommunityFeatures
                .Concat(ProfessionalOnlyFeatures)
                .ToArray(),
            ["enterprise"] = CommunityFeatures
                .Concat(ProfessionalOnlyFeatures)
                .Concat(EnterpriseOnlyFeatures)
                .ToArray(),
            ["enterprise+"] = FeatureCatalog.GetAll()
                .Select(feature => feature.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

    public static string? NormalizeTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return null;
        }

        var normalized = tier.Trim().ToLowerInvariant();
        return TierFeatureMap.ContainsKey(normalized) ? normalized : null;
    }

    public static IReadOnlyList<string> GetFeaturesForTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return Array.Empty<string>();
        }

        return TierFeatureMap.TryGetValue(tier.Trim().ToLowerInvariant(), out var features)
            ? features
            : Array.Empty<string>();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetTierFeatureMap() => TierFeatureMap;
}
