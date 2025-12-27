using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for creating tokens for the Device Authorization Flow.
/// </summary>
public interface IDeviceCodeTokenFactory
{
    /// <summary>
    /// Exchanges a device code for tokens after polling.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(DeviceCodePollRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for device code token polling.
/// </summary>
public record DeviceCodePollRequest(
    string DeviceCode,
    string ClientId,
    string Issuer,
    string? DpopJkt = null
);
