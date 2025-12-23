using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed definition for a claim mapping between external and local claims.
/// </summary>
public sealed record ClaimMappingSeedDefinition
{
    /// <summary>
    /// The claim type from the external identity provider. Required.
    /// </summary>
    [JsonPropertyName("externalClaim")]
    public required string ExternalClaim { get; init; }

    /// <summary>
    /// The claim type to use in the local token. Required.
    /// </summary>
    [JsonPropertyName("localClaim")]
    public required string LocalClaim { get; init; }

    /// <summary>
    /// Optional transformation to apply to the claim value.
    /// Examples: "lowercase", "uppercase", "trim", "split:,", etc.
    /// </summary>
    [JsonPropertyName("transform")]
    public string? Transform { get; init; }

    /// <summary>
    /// Processing order for this mapping.
    /// </summary>
    [JsonPropertyName("order")]
    public int? Order { get; init; }
}
