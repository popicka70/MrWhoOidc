using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Settings;

/// <summary>
/// Represents tenant-specific settings that can override platform defaults.
/// Stored as JSON in Tenant.SettingsJson column.
/// </summary>
public class TenantSettings
{
    /// <summary>
    /// OIDC-specific settings
    /// </summary>
    [JsonPropertyName("oidc")]
    public OidcTenantSettings? Oidc { get; set; }

    /// <summary>
    /// Authentication settings
    /// </summary>
    [JsonPropertyName("auth")]
    public AuthTenantSettings? Auth { get; set; }

    /// <summary>
    /// QR login settings
    /// </summary>
    [JsonPropertyName("qrLogin")]
    public QrLoginTenantSettings? QrLogin { get; set; }

    /// <summary>
    /// Token lifetime settings
    /// </summary>
    [JsonPropertyName("tokens")]
    public TokenTenantSettings? Tokens { get; set; }
}

/// <summary>
/// OIDC-specific tenant settings
/// </summary>
public class OidcTenantSettings
{
    /// <summary>
    /// Override issuer URI (rarely used - usually computed from tenant context)
    /// </summary>
    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    /// <summary>
    /// Whether to require PKCE for authorization code flow
    /// </summary>
    [JsonPropertyName("requirePkce")]
    public bool? RequirePkce { get; set; }

    /// <summary>
    /// Allowed CORS origins for this tenant
    /// </summary>
    [JsonPropertyName("corsOrigins")]
    public List<string>? CorsOrigins { get; set; }
}

/// <summary>
/// Authentication-specific tenant settings
/// </summary>
public class AuthTenantSettings
{
    /// <summary>
    /// Allow refresh token introspection for this tenant
    /// </summary>
    [JsonPropertyName("allowRefreshTokenIntrospection")]
    public bool? AllowRefreshTokenIntrospection { get; set; }

    /// <summary>
    /// Require MFA for all users in this tenant
    /// </summary>
    [JsonPropertyName("requireMfa")]
    public bool? RequireMfa { get; set; }

    /// <summary>
    /// Password policy settings
    /// </summary>
    [JsonPropertyName("passwordPolicy")]
    public PasswordPolicySettings? PasswordPolicy { get; set; }
}

/// <summary>
/// Password policy settings
/// </summary>
public class PasswordPolicySettings
{
    /// <summary>
    /// Minimum password length
    /// </summary>
    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    /// <summary>
    /// Require uppercase letters
    /// </summary>
    [JsonPropertyName("requireUppercase")]
    public bool? RequireUppercase { get; set; }

    /// <summary>
    /// Require lowercase letters
    /// </summary>
    [JsonPropertyName("requireLowercase")]
    public bool? RequireLowercase { get; set; }

    /// <summary>
    /// Require digits
    /// </summary>
    [JsonPropertyName("requireDigit")]
    public bool? RequireDigit { get; set; }

    /// <summary>
    /// Require special characters
    /// </summary>
    [JsonPropertyName("requireSpecialChar")]
    public bool? RequireSpecialChar { get; set; }
}

/// <summary>
/// QR login tenant settings
/// </summary>
public class QrLoginTenantSettings
{
    /// <summary>
    /// Enable/disable QR login for this tenant
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// QR session lifetime in seconds
    /// </summary>
    [JsonPropertyName("sessionLifetimeSeconds")]
    public int? SessionLifetimeSeconds { get; set; }
}

/// <summary>
/// Token lifetime tenant settings
/// </summary>
public class TokenTenantSettings
{
    /// <summary>
    /// Access token lifetime in seconds
    /// </summary>
    [JsonPropertyName("accessTokenLifetimeSeconds")]
    public int? AccessTokenLifetimeSeconds { get; set; }

    /// <summary>
    /// Refresh token lifetime in seconds
    /// </summary>
    [JsonPropertyName("refreshTokenLifetimeSeconds")]
    public int? RefreshTokenLifetimeSeconds { get; set; }

    /// <summary>
    /// Authorization code lifetime in seconds
    /// </summary>
    [JsonPropertyName("authorizationCodeLifetimeSeconds")]
    public int? AuthorizationCodeLifetimeSeconds { get; set; }

    /// <summary>
    /// ID token lifetime in seconds
    /// </summary>
    [JsonPropertyName("idTokenLifetimeSeconds")]
    public int? IdTokenLifetimeSeconds { get; set; }
}
