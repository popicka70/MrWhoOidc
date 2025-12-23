using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed definition for assigning an identity provider to a client.
/// </summary>
public sealed record ClientIdpAssignmentSeedDefinition
{
    /// <summary>
    /// Name of the identity provider to assign. Required.
    /// </summary>
    [JsonPropertyName("providerName")]
    public required string ProviderName { get; init; }

    /// <summary>
    /// Whether this IdP is enabled for the client.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>
    /// Whether this IdP is the default for this client.
    /// </summary>
    [JsonPropertyName("isDefaultForClient")]
    public bool? IsDefaultForClient { get; init; }

    /// <summary>
    /// Whether to auto-redirect to this IdP if it's the only one available.
    /// </summary>
    [JsonPropertyName("autoRedirectIfSingle")]
    public bool? AutoRedirectIfSingle { get; init; }

    /// <summary>
    /// Required ACR value for this IdP assignment.
    /// </summary>
    [JsonPropertyName("requiredAcr")]
    public string? RequiredAcr { get; init; }

    /// <summary>
    /// Display order for this IdP on the client's login page.
    /// </summary>
    [JsonPropertyName("order")]
    public int? Order { get; init; }
}
