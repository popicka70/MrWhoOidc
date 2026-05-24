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

    [JsonPropertyName("v")]
    public int Version { get; set; } = 2;
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
