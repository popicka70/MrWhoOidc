# Token Processing Contracts

**Feature**: 015-auth-architecture-cleanup  
**Domain**: Token Services

## Overview

These contracts define the interfaces for the decomposed token processing services. Each service handles a single grant type, extracted from the monolithic `TokenService`.

---

## IAuthorizationCodeExchanger

Handles `grant_type=authorization_code` from the token endpoint.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Exchanges authorization codes for tokens (code grant flow).
/// </summary>
public interface IAuthorizationCodeExchanger
{
    /// <summary>
    /// Exchanges an authorization code for access/ID tokens.
    /// </summary>
    /// <param name="request">The exchange request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Token result containing tokens or error.</returns>
    Task<TokenResult> ExchangeAsync(
        AuthorizationCodeExchangeRequest request,
        CancellationToken ct = default);
}
```

### Request Type

```csharp
/// <summary>
/// Request parameters for authorization code exchange.
/// </summary>
/// <param name="ClientId">Authenticated client ID.</param>
/// <param name="Code">The authorization code to exchange.</param>
/// <param name="RedirectUri">Redirect URI (must match original request if provided).</param>
/// <param name="CodeVerifier">PKCE code verifier (required if code challenge was used).</param>
/// <param name="DPoPProof">Optional DPoP proof JWT.</param>
/// <param name="TokenEndpointUrl">Token endpoint URL for audience validation.</param>
public record AuthorizationCodeExchangeRequest(
    string ClientId,
    string Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? DPoPProof,
    string TokenEndpointUrl);
```

### Validation Rules

| Field | Rule |
|-------|------|
| ClientId | Required, must match authenticated client |
| Code | Required, not blank, must exist and be unexpired |
| RedirectUri | Required if original authorize request included it |
| CodeVerifier | Required if PKCE was used; validated against stored challenge |
| DPoPProof | If present, validated and used to bind tokens |

### Response

Returns `TokenResult`:
- Success: `TokenResult.Success(accessToken, idToken, refreshToken, expiresIn, tokenType, scope)`
- Error: `TokenResult.Error(error, errorDescription)`

---

## IRefreshTokenExchanger

Handles `grant_type=refresh_token` from the token endpoint.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Exchanges refresh tokens for new access tokens.
/// </summary>
public interface IRefreshTokenExchanger
{
    /// <summary>
    /// Exchanges a refresh token for a new access token and optional new refresh token.
    /// </summary>
    Task<TokenResult> ExchangeAsync(
        RefreshTokenExchangeRequest request,
        CancellationToken ct = default);
}
```

### Request Type

```csharp
/// <summary>
/// Request parameters for refresh token exchange.
/// </summary>
/// <param name="ClientId">Authenticated client ID.</param>
/// <param name="RefreshToken">The refresh token to exchange.</param>
/// <param name="RequestedScopes">Optional scope reduction.</param>
/// <param name="DPoPProof">Optional DPoP proof JWT.</param>
/// <param name="TokenEndpointUrl">Token endpoint URL for audience validation.</param>
public record RefreshTokenExchangeRequest(
    string ClientId,
    string RefreshToken,
    string[]? RequestedScopes,
    string? DPoPProof,
    string TokenEndpointUrl);
```

### Validation Rules

| Field | Rule |
|-------|------|
| ClientId | Required, must match token's original client |
| RefreshToken | Required, must exist, not revoked, not expired |
| RequestedScopes | If provided, must be subset of original scopes |
| DPoPProof | Must match original token binding (if any) |

### Token Rotation

- Current refresh token is revoked upon use
- New refresh token issued with updated expiry
- Rotation is atomic (transaction-protected)

---

## IClientCredentialsTokenFactory

Handles `grant_type=client_credentials` from the token endpoint.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Creates access tokens for client credentials grants.
/// </summary>
public interface IClientCredentialsTokenFactory
{
    /// <summary>
    /// Creates an access token for client credentials flow.
    /// </summary>
    Task<TokenResult> CreateAsync(
        ClientCredentialsRequest request,
        CancellationToken ct = default);
}
```

### Request Type

```csharp
/// <summary>
/// Request parameters for client credentials token creation.
/// </summary>
/// <param name="ClientId">Authenticated client ID.</param>
/// <param name="RequestedScopes">Requested access token scopes.</param>
/// <param name="DPoPProof">Optional DPoP proof JWT.</param>
/// <param name="TokenEndpointUrl">Token endpoint URL for audience validation.</param>
public record ClientCredentialsRequest(
    string ClientId,
    string[] RequestedScopes,
    string? DPoPProof,
    string TokenEndpointUrl);
```

### Validation Rules

| Field | Rule |
|-------|------|
| ClientId | Required, client must be configured for client_credentials grant |
| RequestedScopes | Must be subset of client's allowed scopes |
| DPoPProof | If present, validated and used to bind token |

### Notes

- No refresh token issued (per RFC 6749)
- No ID token issued (no user involved)
- No consent required (machine-to-machine)

---

## IDeviceCodeTokenFactory

Handles `grant_type=urn:ietf:params:oauth:grant-type:device_code` from the token endpoint.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Processes device code polling requests.
/// </summary>
public interface IDeviceCodeTokenFactory
{
    /// <summary>
    /// Processes a device code poll request.
    /// Returns tokens if authorized, or appropriate pending/error status.
    /// </summary>
    Task<TokenResult> ProcessPollAsync(
        DeviceCodePollRequest request,
        CancellationToken ct = default);
}
```

### Request Type

```csharp
/// <summary>
/// Request parameters for device code polling.
/// </summary>
/// <param name="ClientId">Client ID (must match device code request).</param>
/// <param name="DeviceCode">The device code being polled.</param>
/// <param name="DPoPProof">Optional DPoP proof JWT.</param>
/// <param name="TokenEndpointUrl">Token endpoint URL for audience validation.</param>
public record DeviceCodePollRequest(
    string ClientId,
    string DeviceCode,
    string? DPoPProof,
    string TokenEndpointUrl);
```

### Response States

| Device Code State | Response |
|-------------------|----------|
| Not found | `invalid_grant` error |
| Expired | `expired_token` error |
| Pending user auth | `authorization_pending` error |
| Slow poll | `slow_down` error |
| Denied by user | `access_denied` error |
| Authorized | Success with tokens |

---

## Common Types

### TokenResult

```csharp
namespace MrWhoOidc.Auth.Services.Token;

public record TokenResult
{
    public bool IsSuccess { get; init; }
    public string? AccessToken { get; init; }
    public string? IdToken { get; init; }
    public string? RefreshToken { get; init; }
    public int? ExpiresIn { get; init; }
    public string? TokenType { get; init; }
    public string? Scope { get; init; }
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }
    
    public static TokenResult Success(
        string accessToken, 
        string? idToken, 
        string? refreshToken,
        int expiresIn,
        string tokenType,
        string scope) => new()
    {
        IsSuccess = true,
        AccessToken = accessToken,
        IdToken = idToken,
        RefreshToken = refreshToken,
        ExpiresIn = expiresIn,
        TokenType = tokenType,
        Scope = scope
    };
    
    public static TokenResult Error(string error, string? description = null) => new()
    {
        IsSuccess = false,
        Error = error,
        ErrorDescription = description
    };
}
```

---

## Dependencies

All token services depend on:
- `IKeyStore` - for signing keys
- `IJwtService` - for token creation
- `AuthDbContext` - for persistence
- `IOptions<OidcOptions>` - for configuration
- `ILogger<T>` - for logging
- `TimeProvider` - for clock abstraction
