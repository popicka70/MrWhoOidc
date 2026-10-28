using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.Cli.Configuration;

/// <summary>
/// CLI configuration stored in ~/.mrwhooidc/config.json
/// Contains profiles for multiple server connections and authentication state.
/// </summary>
public sealed partial class CliConfig
{
    [JsonPropertyName("currentProfile")]
    public string CurrentProfile { get; set; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new();

    public static string GetConfigDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

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

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // The file disappeared between the existence check and the read: there is nothing to load.
            return new CliConfig();
        }
        catch (DirectoryNotFoundException)
        {
            return new CliConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliConfig>(json, JsonOptions) ?? new CliConfig();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // M-CliCfg: a corrupt config must never be silently replaced by an empty one. The old behaviour
            // returned an empty config, and the next SaveAsync (login, refresh, profile switch) then wrote that
            // empty state back - wiping every profile and token while masking the corruption.
            // The corrupt file is quarantined and the command fails loudly instead.
            var quarantinePath = QuarantineCorruptConfig(filePath);
            throw new InvalidOperationException(
                $"The CLI configuration at '{filePath}' is corrupt ({ex.Message}). " +
                $"It was moved to '{quarantinePath}' and was not overwritten. " +
                "Repair or remove the quarantined file, then run the command again.",
                ex);
        }
    }

    /// <summary>
    /// Moves a corrupt configuration file aside so the operator can inspect it and the next save cannot
    /// silently destroy the remaining state.
    /// </summary>
    private static string QuarantineCorruptConfig(string filePath)
    {
        var quarantinePath = $"{filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(filePath, quarantinePath, overwrite: true);
            return quarantinePath;
        }
        catch (IOException)
        {
            // Could not move it: report the original path so the operator can act on it.
            return filePath;
        }
        catch (UnauthorizedAccessException)
        {
            return filePath;
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var configDir = GetConfigDirectory();
        // 0700 on Unix when created; the config holds access and refresh tokens.
        OwnerOnlyFile.EnsureDirectory(configDir);

        var filePath = GetConfigFilePath();
        var json = JsonSerializer.Serialize(this, JsonOptions);

        // Written to an owner-only (0600) temp file and moved into place, so the tokens are never
        // readable by other users, even briefly, and a crash cannot leave a truncated config.
        // Windows: token fields are additionally DPAPI-protected (CurrentUser) by ProtectedTokenJsonConverter;
        // plaintext values from older versions are migrated on this save.
        await OwnerOnlyFile.WriteAllTextAsync(filePath, json, overwrite: true, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Validates that a profile name is either a codename (alphanumeric + hyphens,
    /// no leading/trailing hyphen, no whitespace) or a valid absolute HTTP/HTTPS URL.
    /// </summary>
    public static bool IsValidProfileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Allow codenames: alphanumeric with optional hyphens in the middle
        if (CodeNameRegex().IsMatch(name))
            return true;

        // Allow valid absolute HTTP/HTTPS URLs (server URLs used as profile names)
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;

        return false;
    }

    /// <summary>
    /// Renames a profile entry. Returns false if the old profile was not found
    /// or the new name already exists.
    /// </summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (!Profiles.TryGetValue(oldName, out var profile))
            return false;

        if (Profiles.ContainsKey(newName))
            return false;

        Profiles.Remove(oldName);
        Profiles[newName] = profile;

        if (string.Equals(CurrentProfile, oldName, StringComparison.Ordinal))
            CurrentProfile = newName;

        return true;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?$")]
    private static partial Regex CodeNameRegex();

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
    [JsonConverter(typeof(ProtectedTokenJsonConverter))]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    [JsonConverter(typeof(ProtectedTokenJsonConverter))]
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
