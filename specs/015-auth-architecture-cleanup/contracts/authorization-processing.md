# Authorization Processing Contracts

**Feature**: 015-auth-architecture-cleanup  
**Domain**: Authorization Services

## Overview

These contracts define the interfaces for the decomposed authorization processing services. Each service handles a specific aspect of the authorization flow, extracted from the monolithic `AuthorizeHandler`.

---

## IAuthorizeRequestValidator

Validates authorization request parameters per RFC 6749 and OpenID Connect Core.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Validates OAuth 2.0 / OIDC authorization request parameters.
/// </summary>
public interface IAuthorizeRequestValidator
{
    /// <summary>
    /// Validates an authorization request and resolves client and redirect URI.
    /// </summary>
    /// <param name="request">The raw authorization request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result with resolved entities or error.</returns>
    Task<AuthorizeValidationResult> ValidateAsync(
        AuthorizeRequest request,
        CancellationToken ct = default);
}
```

### Request Type

```csharp
/// <summary>
/// Raw authorization request parameters from query string or PAR.
/// </summary>
public record AuthorizeRequest(
    string? ClientId,
    string? RedirectUri,
    string? ResponseType,
    string? Scope,
    string? State,
    string? Nonce,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? ResponseMode,
    string? AcrValues,
    string? MaxAge,
    string? IdTokenHint,
    string? LoginHint,
    string? Prompt,
    string? Display,
    string? UiLocales,
    string? Resource,
    string? RequestUri);
```

### Result Type

```csharp
/// <summary>
/// Result of authorization request validation.
/// </summary>
public record AuthorizeValidationResult
{
    public bool IsValid { get; init; }
    public OAuthError? Error { get; init; }
    public string? ErrorDescription { get; init; }
    
    // Resolved entities (only set if IsValid)
    public ClientEntity? Client { get; init; }
    public Uri? ValidatedRedirectUri { get; init; }
    public string[] ValidatedScopes { get; init; } = [];
    public string? RequestedAcr { get; init; }
    public string? Nonce { get; init; }
    public string? State { get; init; }
    public ResponseMode ResponseMode { get; init; }
    public CodeChallengeInfo? PkceInfo { get; init; }
    
    public static AuthorizeValidationResult Valid(/* params */) => /* ... */;
    public static AuthorizeValidationResult Invalid(OAuthError error, string? description = null) => /* ... */;
}

public record CodeChallengeInfo(string Challenge, CodeChallengeMethod Method);

public enum ResponseMode { Query, Fragment, FormPost }
public enum CodeChallengeMethod { Plain, S256 }
```

### Validation Steps

1. **Client Resolution**
   - `client_id` required
   - Client must exist and be enabled

2. **Redirect URI Validation**
   - Must be registered for client
   - Exact match (no wildcards)
   - HTTPS required for non-localhost

3. **Response Type Validation**
   - Must be `code` (authorization code flow)
   - Client must be configured for grant type

4. **Scope Validation**
   - Scopes must be subset of client's allowed scopes
   - `openid` required for OIDC

5. **PKCE Validation**
   - Required for public clients
   - `code_challenge_method=S256` required if PKCE used

6. **Additional Parameters**
   - `nonce` recommended for OIDC
   - `response_mode` must be valid
   - `max_age` must be positive integer if present

---

## IConsentProcessor

Manages user consent state and decisions.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Processes and manages user consent for OAuth/OIDC flows.
/// </summary>
public interface IConsentProcessor
{
    /// <summary>
    /// Evaluates whether consent is required for the given request.
    /// </summary>
    Task<ConsentDecision> EvaluateAsync(
        Guid userId,
        string clientId,
        string[] requestedScopes,
        CancellationToken ct = default);
    
    /// <summary>
    /// Grants consent for the specified scopes.
    /// </summary>
    Task GrantAsync(
        Guid userId,
        string clientId,
        string[] grantedScopes,
        CancellationToken ct = default);
    
    /// <summary>
    /// Revokes all consent for a user-client pair.
    /// </summary>
    Task RevokeAsync(
        Guid userId,
        string clientId,
        CancellationToken ct = default);
    
    /// <summary>
    /// Lists all active consents for a user.
    /// </summary>
    Task<IReadOnlyList<ConsentInfo>> ListUserConsentsAsync(
        Guid userId,
        CancellationToken ct = default);
}
```

### Decision Type

```csharp
/// <summary>
/// Result of consent evaluation.
/// </summary>
public record ConsentDecision
{
    public bool RequiresConsent { get; init; }
    public string[] PreviouslyGrantedScopes { get; init; } = [];
    public string[] NewScopesRequiringConsent { get; init; } = [];
    public DateTimeOffset? LastConsentTime { get; init; }
}
```

### Consent Rules

1. **First-party Clients**: May bypass consent if configured (`RequireConsent = false`)
2. **New Scopes**: Consent required when requesting scopes not previously granted
3. **Expired Consent**: Re-consent required if consent has TTL and expired
4. **Prompt=consent**: Always show consent screen regardless of existing consent

### Transaction Safety

`GrantAsync` must be transactional:
```csharp
// Correct pattern (from research.md)
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await db.Database.BeginTransactionAsync(ct);
    // ... upsert logic ...
    await transaction.CommitAsync(ct);
});
```

---

## IProviderSelectionService

Handles identity provider selection for multi-provider scenarios.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Determines which identity provider to use for authentication.
/// </summary>
public interface IProviderSelectionService
{
    /// <summary>
    /// Determines the appropriate identity provider based on hints and configuration.
    /// </summary>
    Task<ProviderSelectionResult> DetermineProviderAsync(
        string clientId,
        string? idpHint,
        string? acrValues,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets available providers for a client (for selection UI).
    /// </summary>
    Task<IReadOnlyList<ProviderOption>> GetAvailableProvidersAsync(
        string clientId,
        CancellationToken ct = default);
}
```

### Result Types

```csharp
/// <summary>
/// Result of provider determination.
/// </summary>
public record ProviderSelectionResult
{
    public bool RequiresSelection { get; init; }
    public string? SelectedProviderId { get; init; }
    public IReadOnlyList<ProviderOption> AvailableProviders { get; init; } = [];
}

/// <summary>
/// Information about an available identity provider.
/// </summary>
public record ProviderOption(
    string ProviderId,
    string DisplayName,
    string? IconUrl,
    ProviderType Type);

public enum ProviderType { Local, ExternalOidc, ExternalSaml, Passkey }
```

### Selection Logic

1. **Explicit IDP Hint**: Use `idp_hint` parameter if present
2. **ACR Mapping**: Match `acr_values` to provider capabilities
3. **Client Default**: Use client's configured default provider
4. **Single Provider**: Auto-select if only one provider available
5. **Multiple Providers**: Show selection UI if none of above applies

---

## IPkceValidator

Validates PKCE code challenges and verifiers.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Validates PKCE code challenges and verifiers.
/// </summary>
public interface IPkceValidator
{
    /// <summary>
    /// Validates that the code verifier matches the stored code challenge.
    /// </summary>
    /// <param name="codeVerifier">The code verifier from token request.</param>
    /// <param name="codeChallenge">The stored code challenge from authorize.</param>
    /// <param name="method">The challenge method (S256 or plain).</param>
    /// <returns>True if verification succeeds.</returns>
    bool Validate(string codeVerifier, string codeChallenge, CodeChallengeMethod method);
}
```

### Validation Algorithm

For S256:
```csharp
// SHA256(codeVerifier) == Base64UrlDecode(codeChallenge)
var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
var expected = Base64UrlEncoder.Encode(hash);
return expected == codeChallenge;
```

For plain (discouraged):
```csharp
return codeVerifier == codeChallenge;
```

---

## IAuthorizationCodeGenerator

Generates and stores authorization codes.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Generates and persists authorization codes.
/// </summary>
public interface IAuthorizationCodeGenerator
{
    /// <summary>
    /// Creates a new authorization code with associated session data.
    /// </summary>
    Task<string> GenerateAsync(
        AuthorizationCodeData data,
        CancellationToken ct = default);
}

/// <summary>
/// Data to associate with an authorization code.
/// </summary>
public record AuthorizationCodeData(
    string ClientId,
    Guid UserId,
    string[] GrantedScopes,
    string? RedirectUri,
    string? Nonce,
    string? CodeChallenge,
    CodeChallengeMethod? CodeChallengeMethod,
    string? SessionId,
    string? Acr,
    DateTimeOffset AuthTime);
```

### Code Properties

- 256-bit cryptographically random
- Base64URL encoded (43 characters)
- Single-use (deleted after exchange)
- Short-lived (default 10 minutes)

---

## Dependencies

All authorization services depend on:
- `AuthDbContext` - for persistence
- `IClientStore` - for client resolution
- `IOptions<OidcOptions>` - for configuration
- `ILogger<T>` - for logging
- `TimeProvider` - for clock abstraction
