using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for exchanging an authorization code for tokens.
/// </summary>
public interface IAuthorizationCodeExchanger
{
    /// <summary>
    /// Exchanges an authorization code for an access token (and optionally a refresh token).
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAsync(AuthorizationCodeExchangeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for authorization code exchange.
/// </summary>
public record AuthorizationCodeExchangeRequest(
    string Code,
    string RedirectUri,
    string ClientId,
    string CodeVerifier,
    string Issuer,
    string? DpopJkt = null,
    string? IpAddress = null,
    string? UserAgent = null,
    Guid? TenantId = null
);
