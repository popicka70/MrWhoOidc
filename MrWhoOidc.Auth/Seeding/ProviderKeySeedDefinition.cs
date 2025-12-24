using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed definition for an identity provider key (public key for verification).
/// </summary>
public sealed record ProviderKeySeedDefinition
{
    /// <summary>
    /// Key purpose: "signing" or "encryption".
    /// </summary>
    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = "signing";

    /// <summary>
    /// Algorithm identifier (e.g., "RS256", "ES256").
    /// </summary>
    [JsonPropertyName("alg")]
    public string Alg { get; init; } = "RS256";

    /// <summary>
    /// Optional key ID.
    /// </summary>
    [JsonPropertyName("kid")]
    public string? Kid { get; init; }

    /// <summary>
    /// Public key in JWK format (JSON string).
    /// Note: Private keys are never exported.
    /// </summary>
    [JsonPropertyName("jwk")]
    public string? Jwk { get; init; }

    /// <summary>
    /// Whether this key is currently active.
    /// </summary>
    [JsonPropertyName("active")]
    public bool? Active { get; init; }
}
