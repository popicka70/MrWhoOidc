using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed manifest intended to be portable (future import/export).
/// Prefer referencing tenants and realms by name/slug rather than by IDs.
/// </summary>
public sealed record SeedManifest
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("tenants")]
    public List<TenantSeedDefinition> Tenants { get; init; } = [];
}

public sealed record TenantSeedDefinition
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Optional. If omitted, the issuer will be computed from configured base URL + tenant slug.
    /// </summary>
    [JsonPropertyName("issuerUri")]
    public string? IssuerUri { get; init; }

    [JsonPropertyName("adminEmail")]
    public string? AdminEmail { get; init; }

    [JsonPropertyName("billingPlan")]
    public string? BillingPlan { get; init; }

    [JsonPropertyName("realms")]
    public List<RealmSeedDefinition> Realms { get; init; } = [];

    [JsonPropertyName("clients")]
    public List<ClientSeedDefinition> Clients { get; init; } = [];
}

public sealed record RealmSeedDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("allowUnconfirmedLogin")]
    public bool? AllowUnconfirmedLogin { get; init; }
}

public sealed record ClientSeedDefinition
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("clientName")]
    public required string ClientName { get; init; }

    /// <summary>
    /// Realm name within the tenant. Defaults to "admin" if omitted.
    /// </summary>
    [JsonPropertyName("realm")]
    public string? Realm { get; init; }

    [JsonPropertyName("requirePkce")]
    public bool? RequirePkce { get; init; }

    [JsonPropertyName("requireConsent")]
    public bool? RequireConsent { get; init; }

    /// <summary>
    /// One of: No, All, OnlyExternalIdp.
    /// Stored in Clients.AutoApprovalMode.
    /// </summary>
    [JsonPropertyName("autoApprovalMode")]
    public string? AutoApprovalMode { get; init; }

    /// <summary>
    /// Optional initial secret (dev/test). Stored using the existing hashing scheme.
    /// For production import/export, prefer managing secrets via dedicated APIs.
    /// </summary>
    [JsonPropertyName("clientSecret")]
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Optional: name of an environment/config key whose value contains the client secret.
    /// This keeps secrets out of the manifest while allowing env-file driven seeding.
    /// </summary>
    [JsonPropertyName("clientSecretEnv")]
    public string? ClientSecretEnv { get; init; }

    [JsonPropertyName("allowedLoginRedirectUris")]
    public List<string> AllowedLoginRedirectUris { get; init; } = [];

    [JsonPropertyName("allowedLogoutRedirectUris")]
    public List<string> AllowedLogoutRedirectUris { get; init; } = [];

    // OBO / Token Exchange policy (optional)
    // These map to Client.Obo* fields and are only applied when present.
    [JsonPropertyName("oboEnabled")]
    public bool? OboEnabled { get; init; }

    [JsonPropertyName("oboAllowedSourceAudiences")]
    public List<string> OboAllowedSourceAudiences { get; init; } = [];

    [JsonPropertyName("oboAllowedTargetAudiences")]
    public List<string> OboAllowedTargetAudiences { get; init; } = [];

    [JsonPropertyName("oboAllowedScopes")]
    public List<string> OboAllowedScopes { get; init; } = [];
}
