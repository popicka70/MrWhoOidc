using System.Collections.Generic;
using System.Linq;

namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Provides feature metadata for the license generation UI.
/// Mirrors the identifiers in MrWhoOidc.Auth.Licensing.Models.FeatureFlags.
/// </summary>
public static class FeatureCatalog
{
    private static readonly IReadOnlyList<FeatureDefinition> AllFeatures = new List<FeatureDefinition>
    {
        new("basic_oidc", "Basic OIDC", "Core authorization and token endpoints.", false),
        new("basic_admin_ui", "Admin UI", "Administrative portal access.", false),
        new("multi_tenancy", "Multi-tenancy", "Platform-level tenant isolation.", true),
        new("advanced_security", "Advanced security", "Adaptive policies, hardened defaults.", false),
        new("client_secret_rotation", "Client secret rotation", "Rotating secrets per client.", false),
        new("enhanced_audit_logging", "Enhanced audit logging", "Structured audit logs with PII hashing.", false),
        new("unlimited_scale", "Unlimited scale", "Removes scale caps for tenants/users.", false),
        new("dpop", "DPoP", "Demonstration of Proof-of-Possession tokens.", false),
        new("token_exchange", "Token exchange", "RFC 8693 token exchange endpoints.", false),
        new("backchannel_logout", "Back-channel logout", "Back-channel logout token fan-out.", false),
        new("ldap_integration", "LDAP/AD integration", "Enterprise directory integration.", false),
        new("custom_claim_mappings", "Custom claim mappings", "Advanced claim mapping rules.", false),
        new("advanced_monitoring", "Advanced monitoring", "Telemetry/metrics export.", false),
        new("webauthn", "WebAuthn", "Passkey / WebAuthn support.", false),
        new("risk_based_auth", "Risk-based auth", "Risk-aware auth policies.", false),
        new("hsm_integration", "HSM integration", "External HSM key storage integration.", false),
        new("professional_services", "Professional services", "Specialized deployment services.", false)
    };

    public static IReadOnlyList<FeatureDefinition> GetAll() => AllFeatures;

    public static IReadOnlyList<FeatureDefinition> GetPlatformOnly() => AllFeatures.Where(f => f.IsPlatformOnly).ToList();
}

public sealed record FeatureDefinition(string Key, string DisplayName, string Description, bool IsPlatformOnly);
