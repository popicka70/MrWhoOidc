using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for creating tokens for the Device Authorization Flow (RFC 8628).
/// </summary>
public interface IDeviceCodeTokenFactory
{
    /// <summary>
    /// Creates access and refresh tokens after user has authorized the device.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(DeviceCodeTokenRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for device code token creation after user authorization.
/// </summary>
public record DeviceCodeTokenRequest(
    string ClientId,
    Guid UserId,
    string[] Scopes,
    string Audience,
    string Issuer,
    string? DpopJkt = null,
    string? IpAddress = null,
    string? UserAgent = null,
    Guid? TenantId = null,
    string? MtlsX5tS256 = null,
    /// <summary>
    /// The OP session (F1) that approved the request. The refresh token is bound to it, so ending the session
    /// ends the token. NULL when the approval happened in a browser that had no session.
    /// </summary>
    Guid? SessionId = null
);
