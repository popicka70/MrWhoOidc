using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Configuration options for WebAuthn/FIDO2 functionality.
/// </summary>
public sealed class WebAuthnOptions
{
    /// <summary>
    /// Whether WebAuthn/FIDO2 functionality is enabled globally.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The relying party name displayed to users during authentication.
    /// If not specified, defaults to tenant name or "MrWhoOidc".
    /// </summary>
    public string? RelyingPartyName { get; set; }

    /// <summary>
    /// The relying party identifier (domain) for WebAuthn operations.
    /// If not specified, defaults to tenant slug or server domain.
    /// </summary>
    public string? RelyingPartyId { get; set; }

    /// <summary>
    /// List of allowed origins for WebAuthn operations.
    /// If empty, defaults to the configured issuer domain.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Timeout in seconds for registration ceremonies.
    /// </summary>
    [Range(30, 600)]
    public int RegistrationTimeoutSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Timeout in seconds for authentication ceremonies.
    /// </summary>
    [Range(30, 600)]
    public int AuthenticationTimeoutSeconds { get; set; } = 120; // 2 minutes

    /// <summary>
    /// User verification requirement for authentication.
    /// Valid values: "required", "preferred", "discouraged"
    /// </summary>
    public string UserVerification { get; set; } = "preferred";

    /// <summary>
    /// Attestation conveyance preference for registration.
    /// Valid values: "none", "indirect", "direct", "enterprise"
    /// </summary>
    public string AttestationConveyance { get; set; } = "none";

    /// <summary>
    /// Resident key requirement for credentials.
    /// Valid values: "discouraged", "preferred", "required"
    /// </summary>
    public string ResidentKey { get; set; } = "preferred";

    /// <summary>
    /// Whether to enforce authenticator attachment preferences.
    /// If null, allows both platform and cross-platform authenticators.
    /// Valid values: "platform", "cross-platform", null
    /// </summary>
    public string? AuthenticatorAttachment { get; set; }

    /// <summary>
    /// Maximum number of active credentials per user.
    /// </summary>
    [Range(1, 50)]
    public int MaxCredentialsPerUser { get; set; } = 10;

    /// <summary>
    /// Whether to exclude existing credentials during registration to prevent duplicates.
    /// </summary>
    public bool ExcludeExistingCredentials { get; set; } = true;

    /// <summary>
    /// Whether to allow usernameless authentication flows.
    /// </summary>
    public bool AllowUsernamelessAuthentication { get; set; } = true;

    /// <summary>
    /// Lifetime in seconds for challenge sessions stored in cache.
    /// </summary>
    [Range(60, 1800)]
    public int ChallengeSessionLifetimeSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Whether to require WebAuthn for users who have registered credentials.
    /// If true, users with WebAuthn credentials cannot use password authentication.
    /// </summary>
    public bool RequireWebAuthnForRegisteredUsers { get; set; } = false;

    /// <summary>
    /// Default friendly name pattern for new credentials.
    /// Use placeholders: {datetime}, {deviceType}, {transport}
    /// </summary>
    public string DefaultCredentialNamePattern { get; set; } = "Security Key - {datetime:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Whether to automatically enable WebAuthn for new users during registration.
    /// </summary>
    public bool AutoEnableForNewUsers { get; set; } = false;

    /// <summary>
    /// List of allowed credential algorithms (COSE algorithm identifiers).
    /// Empty array allows all algorithms supported by the library.
    /// Common values: -7 (ES256), -257 (RS256), -37 (PS256)
    /// </summary>
    public int[] AllowedCredentialAlgorithms { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Maximum allowed clock skew in seconds when validating timestamps.
    /// </summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to validate AAGUID (Authenticator Attestation GUID) during registration.
    /// </summary>
    public bool ValidateAaguid { get; set; } = false;

    /// <summary>
    /// Optional AAGUID allowlist for registration policy enforcement.
    /// Values can be GUID strings or Base64-encoded 16-byte GUID values.
    /// Empty list means all authenticators are allowed.
    /// </summary>
    public string[] AllowedAaguids { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether to enforce signature counter validation to detect cloned authenticators.
    /// </summary>
    public bool EnforceSignatureCounter { get; set; } = true;

    /// <summary>
    /// Per-tenant overrides for WebAuthn configuration.
    /// Key: tenant slug, Value: override options
    /// </summary>
    public Dictionary<string, WebAuthnTenantOverrides> TenantOverrides { get; set; } = new();
}

/// <summary>
/// Tenant-specific overrides for WebAuthn configuration.
/// Only non-null properties override the global configuration.
/// </summary>
public sealed class WebAuthnTenantOverrides
{
    public bool? Enabled { get; set; }
    public string? RelyingPartyName { get; set; }
    public string? RelyingPartyId { get; set; }
    public string[]? AllowedOrigins { get; set; }
    public int? RegistrationTimeoutSeconds { get; set; }
    public int? AuthenticationTimeoutSeconds { get; set; }
    public string? UserVerification { get; set; }
    public string? AttestationConveyance { get; set; }
    public string? ResidentKey { get; set; }
    public string? AuthenticatorAttachment { get; set; }
    public int? MaxCredentialsPerUser { get; set; }
    public bool? ExcludeExistingCredentials { get; set; }
    public bool? AllowUsernamelessAuthentication { get; set; }
    public bool? RequireWebAuthnForRegisteredUsers { get; set; }
    public string? DefaultCredentialNamePattern { get; set; }
    public bool? AutoEnableForNewUsers { get; set; }
    public int[]? AllowedCredentialAlgorithms { get; set; }
    public bool? ValidateAaguid { get; set; }
    public string[]? AllowedAaguids { get; set; }
    public bool? EnforceSignatureCounter { get; set; }
}
