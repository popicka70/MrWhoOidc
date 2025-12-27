using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for exchanging a refresh token for new tokens.
/// </summary>
public interface IRefreshTokenExchanger
{
    /// <summary>
    /// Exchanges a refresh token for a new access token (and optionally a new refresh token).
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAsync(RefreshTokenExchangeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for refresh token exchange.
/// </summary>
public record RefreshTokenExchangeRequest(
    string RefreshToken,
    string ClientId,
    string Issuer,
    string? DpopJkt = null,
    string? IpAddress = null,
    string? UserAgent = null,
    Guid? TenantId = null
);
