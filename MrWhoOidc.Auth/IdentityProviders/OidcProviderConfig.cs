using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace MrWhoOidc.Auth.IdentityProviders;

// Minimal OIDC provider config schema to validate stored JSON
public sealed class OidcProviderConfig
{
    [Required]
    [Url]
    public string Authority { get; set; } = string.Empty;

    [Url]
    public string? DiscoveryUrl { get; set; }

    [Required]
    public string ClientId { get; set; } = string.Empty;

    public string? ClientSecret { get; set; } // or client assertion key ref

    public string ResponseType { get; set; } = "code";

    public string[] Scopes { get; set; } = new[] { "openid", "profile", "email" };

    public bool UsePKCE { get; set; } = true;
    public bool UseJAR { get; set; } = false;
    public bool UsePAR { get; set; } = false;

    public string? RequestedAcrValues { get; set; }
    public string? Prompt { get; set; }
    public string? ResponseMode { get; set; }

    public int ClockSkewSeconds { get; set; } = 120;

    public TokenValidationOptions TokenValidation { get; set; } = new();

    public bool BackChannelLogout { get; set; } = true;

    public Dictionary<string, string>? ExtraAuthParams { get; set; }

    public static (bool ok, string? error) TryParse(string json, out OidcProviderConfig? cfg)
    {
        cfg = null;
        try
        {
            cfg = JsonSerializer.Deserialize<OidcProviderConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (cfg is null) return (false, "Empty config");
            // Minimal validation
            if (string.IsNullOrWhiteSpace(cfg.Authority)) return (false, "Authority is required");
            if (string.IsNullOrWhiteSpace(cfg.ClientId)) return (false, "ClientId is required");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public sealed class TokenValidationOptions
{
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateAudience { get; set; } = false;
    public bool ValidateLifetime { get; set; } = true;
}
