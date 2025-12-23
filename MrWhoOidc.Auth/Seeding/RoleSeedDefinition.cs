using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed definition for a role within a realm.
/// </summary>
public sealed record RoleSeedDefinition
{
    /// <summary>
    /// Role name. Required.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Name of the realm this role belongs to. Required.
    /// </summary>
    [JsonPropertyName("realmName")]
    public required string RealmName { get; init; }

    /// <summary>
    /// Whether the role is active.
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; init; }
}
