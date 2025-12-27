using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Services.Token;

namespace MrWhoOidc.Auth.Services;

public interface ITokenService
{
    /// <summary>
    /// Exchanges an authorization code for an access token (and optionally a refresh token).
    /// Implements RFC 6749 Section 4.1.3 and includes protection against code reuse.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a refresh token for a new access token (and optionally a new refresh token).
    /// Implements RFC 6749 Section 6 and includes protection against token reuse.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Creates an access token for a client using the client_credentials grant.
    /// Implements RFC 6749 Section 4.4.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default);
}

/// <summary>
/// Orchestrator service that delegates token operations to specialized domain services.
/// </summary>
internal sealed class TokenService(
    IAuthorizationCodeExchanger authCodeExchanger,
    IRefreshTokenExchanger refreshTokenExchanger,
    IClientCredentialsTokenFactory clientCredentialsFactory) : ITokenService
{
    public Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
    {
        var request = new AuthorizationCodeExchangeRequest(code, redirectUri, clientId, codeVerifier, issuer, dpopJkt, ipAddress, userAgent, tenantId);
        return authCodeExchanger.ExchangeAsync(request, ct);
    }

    public Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
    {
        var request = new RefreshTokenExchangeRequest(refreshToken, clientId, issuer, dpopJkt, ipAddress, userAgent, tenantId);
        return refreshTokenExchanger.ExchangeAsync(request, ct);
    }

    public Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default)
    {
        var request = new ClientCredentialsRequest(clientId, audience, requestedScopes, issuer, dpopJkt);
        return clientCredentialsFactory.CreateTokenAsync(request, ct);
    }
}
