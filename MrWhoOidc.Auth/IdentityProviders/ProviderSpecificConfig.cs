using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// Base class for provider-specific configuration stored in the IdentityProvider.ProviderSpecificConfig JSON field.
/// </summary>
public abstract record ProviderSpecificConfigBase
{
    /// <summary>
    /// The provider template this config is for (used for deserialization).
    /// </summary>
    [JsonPropertyName("template")]
    public abstract WellKnownProviderTemplate Template { get; }
}

/// <summary>
/// Microsoft Entra ID specific configuration.
/// </summary>
public sealed record EntraIdProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.MicrosoftEntraId;

    /// <summary>
    /// Tenant type: "common", "organizations", "consumers", or "specific".
    /// </summary>
    [JsonPropertyName("tenantType")]
    public string TenantType { get; init; } = "common";

    /// <summary>
    /// Specific tenant ID or domain (required when TenantType is "specific").
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    /// <summary>
    /// Domain hint to skip domain selection.
    /// </summary>
    [JsonPropertyName("domainHint")]
    public string? DomainHint { get; init; }

    /// <summary>
    /// Login hint for pre-filling the username.
    /// </summary>
    [JsonPropertyName("loginHint")]
    public string? LoginHint { get; init; }

    /// <summary>
    /// Prompt behavior: none, login, consent, select_account.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; } = "select_account";

    /// <summary>
    /// Gets the effective tenant value for the authority URL.
    /// </summary>
    public string GetEffectiveTenant() => TenantType switch
    {
        "specific" when !string.IsNullOrWhiteSpace(TenantId) => TenantId,
        "organizations" => "organizations",
        "consumers" => "consumers",
        _ => "common"
    };
}

/// <summary>
/// Google specific configuration.
/// </summary>
public sealed record GoogleProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Google;

    /// <summary>
    /// Google Workspace hosted domain filter.
    /// </summary>
    [JsonPropertyName("hostedDomain")]
    public string? HostedDomain { get; init; }

    /// <summary>
    /// Login hint for pre-filling the email.
    /// </summary>
    [JsonPropertyName("loginHint")]
    public string? LoginHint { get; init; }

    /// <summary>
    /// Prompt behavior: none, consent, select_account.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; } = "select_account";

    /// <summary>
    /// Access type: online or offline (for refresh tokens).
    /// </summary>
    [JsonPropertyName("accessType")]
    public string AccessType { get; init; } = "online";
}

/// <summary>
/// Facebook specific configuration.
/// </summary>
public sealed record FacebookProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Facebook;

    /// <summary>
    /// Facebook Graph API version.
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "v19.0";

    /// <summary>
    /// Enable re-authorization flow.
    /// </summary>
    [JsonPropertyName("enableReauthorization")]
    public bool EnableReauthorization { get; init; }
}

/// <summary>
/// Apple specific configuration.
/// </summary>
public sealed record AppleProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Apple;

    /// <summary>
    /// Apple Developer Team ID.
    /// </summary>
    [JsonPropertyName("teamId")]
    public string TeamId { get; init; } = string.Empty;

    /// <summary>
    /// Sign in with Apple private key ID.
    /// </summary>
    [JsonPropertyName("keyId")]
    public string KeyId { get; init; } = string.Empty;

    /// <summary>
    /// ES256 private key in PEM format.
    /// Note: Should be encrypted at rest in production.
    /// </summary>
    [JsonPropertyName("privateKey")]
    public string PrivateKey { get; init; } = string.Empty;

    /// <summary>
    /// Client secret expiry in days (Apple requires regeneration every 6 months max).
    /// </summary>
    [JsonPropertyName("clientSecretExpiryDays")]
    public int ClientSecretExpiryDays { get; init; } = 180;
}

/// <summary>
/// GitHub specific configuration.
/// </summary>
public sealed record GitHubProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.GitHub;

    /// <summary>
    /// Comma-separated list of allowed GitHub organizations.
    /// </summary>
    [JsonPropertyName("allowedOrganizations")]
    public string? AllowedOrganizations { get; init; }

    /// <summary>
    /// Comma-separated list of allowed GitHub teams (format: org/team).
    /// </summary>
    [JsonPropertyName("allowedTeams")]
    public string? AllowedTeams { get; init; }

    /// <summary>
    /// Accept private/noreply email addresses.
    /// </summary>
    [JsonPropertyName("allowPrivateEmails")]
    public bool AllowPrivateEmails { get; init; } = true;
}

/// <summary>
/// LinkedIn specific configuration (minimal - uses standard OIDC).
/// </summary>
public sealed record LinkedInProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.LinkedIn;

    // LinkedIn uses standard OIDC, no special config needed currently
}

/// <summary>
/// Okta specific configuration.
/// </summary>
public sealed record OktaProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Okta;

    /// <summary>
    /// Okta domain (without .okta.com).
    /// </summary>
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// Authorization server ID (default for org authorization server).
    /// </summary>
    [JsonPropertyName("authorizationServerId")]
    public string? AuthorizationServerId { get; init; }
}

/// <summary>
/// Auth0 specific configuration.
/// </summary>
public sealed record Auth0ProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Auth0;

    /// <summary>
    /// Auth0 tenant name.
    /// </summary>
    [JsonPropertyName("tenant")]
    public string Tenant { get; init; } = string.Empty;

    /// <summary>
    /// Custom domain (optional).
    /// </summary>
    [JsonPropertyName("customDomain")]
    public string? CustomDomain { get; init; }
}

/// <summary>
/// Keycloak specific configuration.
/// </summary>
public sealed record KeycloakProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.Keycloak;

    /// <summary>
    /// Keycloak server host.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Keycloak realm name.
    /// </summary>
    [JsonPropertyName("realm")]
    public string Realm { get; init; } = string.Empty;
}

/// <summary>
/// AWS Cognito specific configuration.
/// </summary>
public sealed record AwsCognitoProviderConfig : ProviderSpecificConfigBase
{
    public override WellKnownProviderTemplate Template => WellKnownProviderTemplate.AwsCognito;

    /// <summary>
    /// AWS region.
    /// </summary>
    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Cognito User Pool ID.
    /// </summary>
    [JsonPropertyName("userPoolId")]
    public string UserPoolId { get; init; } = string.Empty;
}
