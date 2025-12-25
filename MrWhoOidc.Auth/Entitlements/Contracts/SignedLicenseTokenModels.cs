using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Entitlements.Contracts;

/// <summary>
/// Request to generate a signed license token for embedding in access tokens.
/// </summary>
public sealed class SignedLicenseTokenRequest
{
    /// <summary>
    /// User or entity identifier (maps to JWT sub claim).
    /// </summary>
    [JsonPropertyName("subjectId")]
    public required string SubjectId { get; init; }

    /// <summary>
    /// Product identifier (maps to JWT aud claim).
    /// </summary>
    [JsonPropertyName("productKey")]
    public required string ProductKey { get; init; }

    /// <summary>
    /// Optional tenant identifier for multi-tenant scenarios.
    /// </summary>
    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }
}

/// <summary>
/// Response containing the signed license token.
/// </summary>
public sealed class SignedLicenseTokenResponse
{
    /// <summary>
    /// Compact JWT (ES256 signed) containing license entitlements.
    /// </summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>
    /// Token expiration timestamp (ISO 8601).
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Key ID used for signing (matches JWT header kid).
    /// </summary>
    [JsonPropertyName("kid")]
    public required string Kid { get; init; }
}

/// <summary>
/// Error response when token generation fails.
/// </summary>
public sealed class SignedLicenseTokenError
{
    /// <summary>
    /// Machine-readable error code.
    /// </summary>
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    /// <summary>
    /// Human-readable error description.
    /// </summary>
    [JsonPropertyName("error_description")]
    public required string ErrorDescription { get; init; }
}

/// <summary>
/// Result of signed license token request with success/failure discriminator.
/// </summary>
public sealed class SignedLicenseTokenResult
{
    public bool Success { get; init; }
    public SignedLicenseTokenResponse? Response { get; init; }
    public SignedLicenseTokenError? Error { get; init; }

    public static SignedLicenseTokenResult Ok(SignedLicenseTokenResponse response) =>
        new() { Success = true, Response = response };

    public static SignedLicenseTokenResult Fail(SignedLicenseTokenError error) =>
        new() { Success = false, Error = error };

    public static SignedLicenseTokenResult Fail(string error, string description) =>
        new() { Success = false, Error = new SignedLicenseTokenError { Error = error, ErrorDescription = description } };
}
