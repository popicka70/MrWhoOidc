using System.Text.Json.Serialization;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// State model protected in the state parameter during external OIDC flow.
/// </summary>
public sealed class StateModel
{
    public string Provider { get; set; } = string.Empty;
    public Guid? ProviderId { get; set; }
    public Guid? TenantId { get; set; }
    public bool IsPlatformProvider { get; set; }
    public string CodeVerifier { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public string? Nonce { get; set; }
    public string? ClientId { get; set; }

    [JsonPropertyName("cid_ref")]
    public string? CorrelationHandle { get; set; }

    public string? CorrelationId { get; set; }

    public bool IsLinking { get; set; }
    public Guid? TargetUserId { get; set; }

    /// <summary>SHA-256 (base64url) of the browser-binding nonce cookie set at start.</summary>
    [JsonPropertyName("bh")]
    public string? BrowserBindingHash { get; set; }

    /// <summary>Unix time (seconds) the state was issued; bounds the state lifetime.</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; set; }

    [JsonPropertyName("v")]
    public int Version { get; set; } = 3;
}

/// <summary>
/// Model for account linking confirmation token.
/// </summary>
public sealed class ConfirmModel
{
    public string Provider { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public Guid TargetUserId { get; set; }
    public string? ReturnUrl { get; set; }
    public string? ClientId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    /// <summary>Random value also set as a cookie in the browser that completed the external sign-in.</summary>
    public string? BrowserBinding { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Snapshot of correlation state.
/// </summary>
public readonly record struct CorrelationSnapshot(string CorrelationId, string Handle);

/// <summary>
/// Result of correlation resolution.
/// </summary>
public readonly record struct CorrelationResolutionResult(
    bool Success,
    string CorrelationId,
    string Handle,
    bool FromHandle,
    bool HandleStale);
