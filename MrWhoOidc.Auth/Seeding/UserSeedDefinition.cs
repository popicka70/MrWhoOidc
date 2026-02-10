using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

public sealed record UserSeedDefinition
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("passwordEnv")]
    public string? PasswordEnv { get; init; }

    [JsonPropertyName("emailVerified")]
    public bool? EmailVerified { get; init; }

    /// <summary>
    /// List of role assignments.
    /// Format: { "role": "admin", "realm": "admin" }
    /// </summary>
    [JsonPropertyName("roles")]
    public List<UserRoleSeedAssignment> Roles { get; init; } = [];

    /// <summary>
    /// List of client assignments.
    /// Format: { "clientId": "my-client", "realm": "default" }
    /// </summary>
    [JsonPropertyName("clients")]
    public List<UserClientSeedAssignment> Clients { get; init; } = [];
}

public sealed record UserRoleSeedAssignment
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("realm")]
    public string? Realm { get; init; }
}

public sealed record UserClientSeedAssignment
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("realm")]
    public string? Realm { get; init; }
}
