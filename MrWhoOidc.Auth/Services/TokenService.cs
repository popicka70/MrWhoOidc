using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Services.Token;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Orchestrator service for token-related operations.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Exchanges an authorization code for an access token (and optionally a refresh token).
    /// Implements RFC 6749 Section 4.1.3 and includes protection against code reuse.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="redirectUri">The redirect URI.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="codeVerifier">The PKCE code verifier.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="dpopJkt">Optional DPoP JWK thumbprint.</param>
    /// <param name="ipAddress">Optional IP address of the requester.</param>
    /// <param name="userAgent">Optional user agent of the requester.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the success status, payload, error, and HTTP status code.</returns>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a refresh token for a new access token (and optionally a new refresh token).
    /// Implements RFC 6749 Section 6 and includes protection against token reuse.
    /// </summary>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="dpopJkt">Optional DPoP JWK thumbprint.</param>
    /// <param name="ipAddress">Optional IP address of the requester.</param>
    /// <param name="userAgent">Optional user agent of the requester.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the success status, payload, error, and HTTP status code.</returns>
    Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Creates an access token for a client using the client_credentials grant.
    /// Implements RFC 6749 Section 4.4.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="audience">The requested audience.</param>
    /// <param name="requestedScopes">The requested scopes.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="dpopJkt">Optional DPoP JWK thumbprint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the success status, payload, error, and HTTP status code.</returns>
    Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, string? mtlsX5tS256 = null, CancellationToken ct = default);

    /// <summary>
    /// Creates an access token for a user after they authorized a device authorization grant (RFC 8628).
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="userId">The user ID who authorized the device.</param>
    /// <param name="scopes">The authorized scopes.</param>
    /// <param name="audience">The requested audience/resource.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="dpopJkt">Optional DPoP JWK thumbprint.</param>
    /// <param name="ipAddress">Optional IP address of the device.</param>
    /// <param name="userAgent">Optional user agent of the device.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the success status, payload, error, and HTTP status code.</returns>
    Task<(bool ok, object? payload, string? error, int status)> CreateDeviceCodeTokenAsync(
        string clientId, Guid userId, string[] scopes, string audience, string issuer,
        string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);
}

/// <summary>
/// Orchestrator service that delegates token operations to specialized domain services.
/// </summary>
internal sealed class TokenService(
    IAuthorizationCodeExchanger authCodeExchanger,
    IRefreshTokenExchanger refreshTokenExchanger,
    IClientCredentialsTokenFactory clientCredentialsFactory,
    IDeviceCodeTokenFactory deviceCodeFactory) : ITokenService
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
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, string? mtlsX5tS256 = null, CancellationToken ct = default)
    {
        var request = new ClientCredentialsRequest(clientId, audience, requestedScopes, issuer, dpopJkt, mtlsX5tS256);
        return clientCredentialsFactory.CreateTokenAsync(request, ct);
    }

    public Task<(bool ok, object? payload, string? error, int status)> CreateDeviceCodeTokenAsync(
        string clientId, Guid userId, string[] scopes, string audience, string issuer,
        string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
    {
        var request = new DeviceCodeTokenRequest(clientId, userId, scopes, audience, issuer, dpopJkt, ipAddress, userAgent, tenantId);
        return deviceCodeFactory.CreateTokenAsync(request, ct);
    }
}
