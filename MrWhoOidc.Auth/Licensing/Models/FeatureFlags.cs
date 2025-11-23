using System;
using System.Collections.Generic;

namespace MrWhoOidc.Auth.Licensing.Models;

public static class FeatureFlags
{
    // Basic features (Community+)
    public const string BasicOidc = "basic_oidc";
    public const string BasicAdminUi = "basic_admin_ui";

    // Professional features
    public const string MultiTenancy = "multi_tenancy";
    public const string AdvancedSecurity = "advanced_security";
    public const string ClientSecretRotation = "client_secret_rotation";
    public const string EnhancedAuditLogging = "enhanced_audit_logging";

    // Enterprise features
    public const string UnlimitedScale = "unlimited_scale";
    public const string DPoP = "dpop";
    public const string TokenExchange = "token_exchange";
    public const string BackchannelLogout = "backchannel_logout";
    public const string LdapIntegration = "ldap_integration";
    public const string CustomClaimMappings = "custom_claim_mappings";
    public const string AdvancedMonitoring = "advanced_monitoring";

    // Enterprise+ features
    public const string WebAuthn = "webauthn";
    public const string RiskBasedAuth = "risk_based_auth";
    public const string HsmIntegration = "hsm_integration";
    public const string ProfessionalServices = "professional_services";

    public static IReadOnlySet<string> AllFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        BasicOidc,
        BasicAdminUi,
        MultiTenancy,
        AdvancedSecurity,
        ClientSecretRotation,
        EnhancedAuditLogging,
        UnlimitedScale,
        DPoP,
        TokenExchange,
        BackchannelLogout,
        LdapIntegration,
        CustomClaimMappings,
        AdvancedMonitoring,
        WebAuthn,
        RiskBasedAuth,
        HsmIntegration,
        ProfessionalServices
    };

    public static IReadOnlySet<string> PlatformOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MultiTenancy
    };

    public static bool IsPlatformOnlyFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return PlatformOnlyFeatures.Contains(featureName);
    }

    public static IReadOnlySet<string> GetFeaturesForTier(LicenseTier tier)
    {
        return tier switch
        {
            LicenseTier.Community => new HashSet<string>(new[]
            {
                BasicOidc,
                BasicAdminUi
            }, StringComparer.OrdinalIgnoreCase),
            LicenseTier.Professional => new HashSet<string>(new[]
            {
                BasicOidc,
                BasicAdminUi,
                MultiTenancy,
                AdvancedSecurity,
                ClientSecretRotation,
                EnhancedAuditLogging
            }, StringComparer.OrdinalIgnoreCase),
            LicenseTier.Enterprise => new HashSet<string>(new[]
            {
                BasicOidc,
                BasicAdminUi,
                MultiTenancy,
                AdvancedSecurity,
                ClientSecretRotation,
                EnhancedAuditLogging,
                UnlimitedScale,
                DPoP,
                TokenExchange,
                BackchannelLogout,
                LdapIntegration,
                CustomClaimMappings,
                AdvancedMonitoring
            }, StringComparer.OrdinalIgnoreCase),
            LicenseTier.EnterprisePlus => new HashSet<string>(AllFeatures, StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
