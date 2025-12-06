using System.Text.Json.Serialization;

namespace LicensingService.Web.Models;

/// <summary>
/// Request to validate a license token.
/// </summary>
public class ValidateLicenseRequest
{
    /// <summary>The JWT license token to validate.</summary>
    public required string Token { get; init; }

    /// <summary>Expected product identifier (optional, validates audience claim).</summary>
    public string? ProductIdentifier { get; init; }

    /// <summary>Whether to check database for revocation status.</summary>
    public bool CheckDatabase { get; init; } = false;
}

/// <summary>
/// Response from license validation.
/// </summary>
public class ValidateLicenseResponse
{
    /// <summary>Whether the token is valid.</summary>
    [JsonPropertyName("valid")]
    public bool IsValid { get; init; }

    /// <summary>Whether the license is currently active (valid and not expired/revoked).</summary>
    [JsonPropertyName("active")]
    public bool IsActive { get; init; }

    /// <summary>Error message if validation failed.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>Error code for programmatic handling.</summary>
    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>License details if valid.</summary>
    [JsonPropertyName("license")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValidatedLicenseInfo? License { get; init; }
}

/// <summary>
/// License information extracted from a validated token.
/// </summary>
public class ValidatedLicenseInfo
{
    /// <summary>Token ID (jti claim).</summary>
    [JsonPropertyName("token_id")]
    public required string TokenId { get; init; }

    /// <summary>Customer identifier.</summary>
    [JsonPropertyName("customer")]
    public required string CustomerIdentifier { get; init; }

    /// <summary>Product identifier.</summary>
    [JsonPropertyName("product")]
    public required string ProductIdentifier { get; init; }

    /// <summary>License tier.</summary>
    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    /// <summary>License scope.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>When the license becomes valid.</summary>
    [JsonPropertyName("valid_from")]
    public DateTimeOffset ValidFrom { get; init; }

    /// <summary>When the license expires.</summary>
    [JsonPropertyName("valid_until")]
    public DateTimeOffset ValidUntil { get; init; }

    /// <summary>Days until expiration (negative if expired).</summary>
    [JsonPropertyName("days_until_expiry")]
    public int DaysUntilExpiry { get; init; }

    /// <summary>License options.</summary>
    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Options { get; init; }

    /// <summary>Database status if checked.</summary>
    [JsonPropertyName("database_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatabaseStatus { get; init; }

    /// <summary>Whether the license is revoked.</summary>
    [JsonPropertyName("revoked")]
    public bool IsRevoked { get; init; }
}

/// <summary>
/// Introspection request (RFC 7662 style).
/// </summary>
public class IntrospectRequest
{
    /// <summary>The token to introspect.</summary>
    public required string Token { get; init; }

    /// <summary>Token type hint (always "license_token").</summary>
    public string? TokenTypeHint { get; init; }
}

/// <summary>
/// Introspection response (RFC 7662 style).
/// </summary>
public class IntrospectResponse
{
    /// <summary>Whether the token is active.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>Token subject (customer identifier).</summary>
    [JsonPropertyName("sub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sub { get; init; }

    /// <summary>Token audience (product identifier).</summary>
    [JsonPropertyName("aud")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Aud { get; init; }

    /// <summary>Token issuer.</summary>
    [JsonPropertyName("iss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Iss { get; init; }

    /// <summary>Token ID.</summary>
    [JsonPropertyName("jti")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Jti { get; init; }

    /// <summary>Expiration time (Unix timestamp).</summary>
    [JsonPropertyName("exp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Exp { get; init; }

    /// <summary>Issued at time (Unix timestamp).</summary>
    [JsonPropertyName("iat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Iat { get; init; }

    /// <summary>Not before time (Unix timestamp).</summary>
    [JsonPropertyName("nbf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Nbf { get; init; }

    /// <summary>Token type.</summary>
    [JsonPropertyName("token_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenType { get; init; }

    /// <summary>License tier.</summary>
    [JsonPropertyName("tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tier { get; init; }

    /// <summary>License scope.</summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
}
