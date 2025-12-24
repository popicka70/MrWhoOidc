using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed definition for an identity provider configuration.
/// </summary>
public sealed record IdentityProviderSeedDefinition
{
    /// <summary>
    /// Unique name within the tenant. Required.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// Provider type: "oidc" or "saml".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "oidc";

    /// <summary>
    /// Whether the provider is enabled for authentication.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>
    /// Whether this is the default provider for the tenant.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; init; }

    /// <summary>
    /// URL to the provider's logo image.
    /// </summary>
    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; init; }

    /// <summary>
    /// Display sort order.
    /// </summary>
    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; init; }

    /// <summary>
    /// Provider-specific configuration (JSON object).
    /// For OIDC: authority, clientId, clientSecret, scopes, etc.
    /// </summary>
    [JsonPropertyName("config")]
    public Dictionary<string, object?>? Config { get; init; }

    /// <summary>
    /// Claim mappings for transforming upstream claims to local claims.
    /// </summary>
    [JsonPropertyName("claimMappings")]
    public List<ClaimMappingSeedDefinition> ClaimMappings { get; init; } = [];

    /// <summary>
    /// Public keys for signature verification.
    /// </summary>
    [JsonPropertyName("keys")]
    public List<ProviderKeySeedDefinition> Keys { get; init; } = [];
}
