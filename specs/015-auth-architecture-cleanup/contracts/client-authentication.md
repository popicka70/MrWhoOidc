# Client Authentication Contracts

**Feature**: 015-auth-architecture-cleanup  
**Domain**: Client Authentication Services

## Overview

This contract defines the interface for client authentication logic extracted from the WebAuth layer into Auth. The goal is to separate HTTP parameter extraction (WebAuth) from credential validation (Auth).

---

## IClientAuthenticationService

Pure domain logic for validating client credentials.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Validates client credentials independent of HTTP context.
/// </summary>
public interface IClientAuthenticationService
{
    /// <summary>
    /// Validates client credentials and returns authentication result.
    /// </summary>
    /// <param name="input">Credential input extracted from request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Authentication result with client entity if successful.</returns>
    Task<ClientAuthResult> ValidateCredentialsAsync(
        ClientCredentialInput input,
        CancellationToken ct = default);
}
```

### Input Type

```csharp
namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Client credential input for authentication.
/// </summary>
/// <param name="ClientId">Client identifier.</param>
/// <param name="ClientSecret">Client secret (for client_secret_basic/post).</param>
/// <param name="ClientAssertion">JWT assertion (for private_key_jwt).</param>
/// <param name="ClientAssertionType">Assertion type URI.</param>
/// <param name="AudienceUrl">Token endpoint URL for JWT audience validation.</param>
public record ClientCredentialInput(
    string ClientId,
    string? ClientSecret,
    string? ClientAssertion,
    string? ClientAssertionType,
    string AudienceUrl);
```

### Result Type

```csharp
namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Result of client authentication attempt.
/// </summary>
public record ClientAuthResult
{
    public bool IsAuthenticated { get; init; }
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }
    public ClientEntity? Client { get; init; }
    public ClientAuthMethod AuthenticationMethod { get; init; }
    
    public static ClientAuthResult Success(ClientEntity client, ClientAuthMethod method) 
        => new() 
        { 
            IsAuthenticated = true, 
            Client = client, 
            AuthenticationMethod = method 
        };
    
    public static ClientAuthResult Failure(string error, string? description = null) 
        => new() 
        { 
            IsAuthenticated = false, 
            Error = error, 
            ErrorDescription = description,
            AuthenticationMethod = ClientAuthMethod.None
        };
}

/// <summary>
/// Client authentication method used.
/// </summary>
public enum ClientAuthMethod
{
    None,
    ClientSecretBasic,
    ClientSecretPost,
    PrivateKeyJwt,
    SelfSignedTlsClientAuth,
    PublicClient
}
```

---

## Authentication Methods

### client_secret_basic

HTTP Basic authentication with client_id:client_secret.

```
Authorization: Basic base64(client_id:client_secret)
```

**Validation**:
1. Decode Base64
2. Split on first `:` (client_id may not contain `:`)
3. URL-decode both parts
4. Lookup client by ID
5. Verify secret against stored hash (Argon2id preferred, BCrypt fallback)

### client_secret_post

Credentials in request body.

```
client_id=myclient&client_secret=mysecret
```

**Validation**:
1. Extract from form body
2. Lookup client by ID
3. Verify secret (same as basic)

### private_key_jwt

JWT assertion signed by client's private key.

```
client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
client_assertion=eyJhbGciOiJSUzI1NiJ9.eyJpc3Mi...
```

**Validation**:
1. Verify `client_assertion_type` is `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`
2. Decode JWT header to get `kid`
3. Retrieve client's JWKS from registration
4. Find key by `kid`
5. Verify JWT signature
6. Validate claims:
   - `iss` = client_id
   - `sub` = client_id
   - `aud` = token endpoint URL
   - `exp` > now
   - `iat` < now + skew
   - `jti` unique (optional: track for replay prevention)

### public (no authentication)

For public clients (SPAs, mobile apps).

**Validation**:
1. Lookup client by ID
2. Verify client is configured as public (`ClientType = "public"`)
3. Require PKCE for authorization code flow

---

## Multi-Secret Support

Clients can have multiple active secrets for zero-downtime rotation.

### Validation Algorithm

```csharp
// Try each non-expired secret until one matches
var secrets = await GetActiveSecrets(clientId);
foreach (var secret in secrets.Where(s => s.ExpiresAt > now))
{
    if (VerifySecret(providedSecret, secret.Hash))
    {
        // Record which secret was used (for rotation tracking)
        await RecordSecretUsage(secret.Id);
        return Success(client, ClientAuthMethod.ClientSecretBasic);
    }
}
return Failure("invalid_client", "Invalid client credentials");
```

### Expiry Warning

When a secret will expire within 7 days:
- Log warning with structured data
- Increment `client_secret_expiry_warning` meter
- Continue authentication (don't fail)

---

## WebAuth Adapter

The WebAuth layer retains the HTTP-specific adapter:

```csharp
namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Extracts client credentials from HTTP and delegates to Auth service.
/// </summary>
public interface IClientAuthenticator
{
    Task<ClientAuthenticationResult> AuthenticateAsync(
        HttpContext context,
        CancellationToken ct = default);
}

public class ClientAuthenticator(IClientAuthenticationService authService) : IClientAuthenticator
{
    public async Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        // 1. Extract credentials from HTTP
        var input = ExtractCredentials(context);
        
        // 2. Delegate to Auth layer
        var result = await authService.ValidateCredentialsAsync(input, ct);
        
        // 3. Map to HTTP-aware result
        return MapToHttpResult(result);
    }
    
    private ClientCredentialInput ExtractCredentials(HttpContext context)
    {
        // Try Authorization header (Basic)
        if (TryExtractBasicAuth(context, out var clientId, out var secret))
            return new(clientId, secret, null, null, GetTokenEndpointUrl(context));
        
        // Try form body
        var form = context.Request.Form;
        return new(
            form["client_id"].ToString(),
            form["client_secret"].ToString(),
            form["client_assertion"].ToString(),
            form["client_assertion_type"].ToString(),
            GetTokenEndpointUrl(context));
    }
}
```

---

## Error Responses

Per RFC 6749 Section 5.2:

| Error | Description |
|-------|-------------|
| `invalid_client` | Client authentication failed |
| `unauthorized_client` | Client not authorized for this grant type |

### Error Details (not returned to client)

- "Client not found" → `invalid_client`
- "Client disabled" → `invalid_client`  
- "Invalid secret" → `invalid_client`
- "All secrets expired" → `invalid_client`
- "JWT signature invalid" → `invalid_client`
- "JWT expired" → `invalid_client`
- "Grant type not allowed" → `unauthorized_client`

---

## Dependencies

- `IClientStore` - for client lookup
- `ISecretHasher` - for secret verification
- `IOptions<OidcOptions>` - for JWT validation
- `ILogger<T>` - for logging
- `IClientSecretMetrics` - for metrics
- `TimeProvider` - for clock
