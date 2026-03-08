using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Cli.Configuration;

/// <summary>
/// CLI configuration stored in ~/.mrwhooidc/config.json
/// Contains profiles for multiple server connections and authentication state.
/// </summary>
public sealed class CliConfig
{
    [JsonPropertyName("currentProfile")]
    public string CurrentProfile { get; set; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new();

    public static string GetConfigDirectory()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".mrwhooidc");
    }

    public static string GetConfigFilePath()
    {
        return Path.Combine(GetConfigDirectory(), "config.json");
    }

    public static async Task<CliConfig> LoadAsync(CancellationToken ct = default)
    {
        var filePath = GetConfigFilePath();
        
        if (!File.Exists(filePath))
        {
            return new CliConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<CliConfig>(json, JsonOptions) ?? new CliConfig();
        }
        catch
        {
            // Corrupt config, return new instance
            return new CliConfig();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var configDir = GetConfigDirectory();
        Directory.CreateDirectory(configDir);

        var filePath = GetConfigFilePath();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);

        // Set restrictive permissions (Unix-like systems)
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Ignore if not supported
            }
        }
    }

    public ProfileConfig? GetCurrentProfile()
    {
        return Profiles.TryGetValue(CurrentProfile, out var profile) ? profile : null;
    }

    public void SetProfile(string name, ProfileConfig profile)
    {
        Profiles[name] = profile;
        CurrentProfile = name;
    }

    public bool RemoveProfile(string name)
    {
        var removed = Profiles.Remove(name);
        
        // Switch to another profile if current was removed
        if (removed && CurrentProfile == name)
        {
            CurrentProfile = Profiles.Keys.FirstOrDefault() ?? "default";
        }
        
        return removed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Configuration profile for a single server connection.
/// </summary>
public sealed class ProfileConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("tokenExpiry")]
    public DateTimeOffset? TokenExpiry { get; set; }

    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    [JsonPropertyName("isPlatformAdmin")]
    public bool IsPlatformAdmin { get; set; }

    [JsonPropertyName("tokenIntrospectedAt")]
    public DateTimeOffset? TokenIntrospectedAt { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) || !string.IsNullOrEmpty(RefreshToken);

    public bool IsTokenExpired => TokenExpiry.HasValue && TokenExpiry.Value <= DateTimeOffset.UtcNow;
}
