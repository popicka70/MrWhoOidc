using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Placeholder implementation of IDeviceCodeTokenFactory.
/// </summary>
public sealed class DeviceCodeTokenFactory : IDeviceCodeTokenFactory
{
    public Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(DeviceCodePollRequest request, CancellationToken ct = default)
    {
        return Task.FromResult<(bool ok, object? payload, string? error, int status)>(
            (false, new { error = "invalid_request", error_description = "Device flow not yet implemented" }, "invalid_request", 400));
    }
}
