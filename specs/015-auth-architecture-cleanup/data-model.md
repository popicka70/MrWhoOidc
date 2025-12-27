# Data Model: Auth Architecture Cleanup

**Feature**: 015-auth-architecture-cleanup  
**Date**: 2025-12-27

## Overview

This document describes the new interfaces and service contracts introduced by the architectural refactoring. No database schema changes are required—this refactoring focuses on code structure, not data persistence.

---

## New Interfaces

### Token Processing Services

#### IAuthorizationCodeExchanger

**Location**: `MrWhoOidc.Auth/Services/Token/IAuthorizationCodeExchanger.cs`

**Purpose**: Handles the authorization code grant type from the token endpoint.

```csharp
namespace MrWhoOidc.Auth.Services.Token;

public interface IAuthorizationCodeExchanger
{
    Task<TokenResult> ExchangeAsync(
        AuthorizationCodeExchangeRequest request,
        CancellationToken ct = default);
}

public record AuthorizationCodeExchangeRequest(
    string ClientId,
    string Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? DPoPProof,
    string TokenEndpointUrl);
```

---

#### IRefreshTokenExchanger

**Location**: `MrWhoOidc.Auth/Services/Token/IRefreshTokenExchanger.cs`

**Purpose**: Handles refresh token grant type from the token endpoint.

```csharp
namespace MrWhoOidc.Auth.Services.Token;

public interface IRefreshTokenExchanger
{
    Task<TokenResult> ExchangeAsync(
        RefreshTokenExchangeRequest request,
        CancellationToken ct = default);
}

public record RefreshTokenExchangeRequest(
    string ClientId,
    string RefreshToken,
    string[]? RequestedScopes,
    string? DPoPProof,
    string TokenEndpointUrl);
```

---

#### IClientCredentialsTokenFactory

**Location**: `MrWhoOidc.Auth/Services/Token/IClientCredentialsTokenFactory.cs`

**Purpose**: Handles client credentials grant type from the token endpoint.

```csharp
namespace MrWhoOidc.Auth.Services.Token;

public interface IClientCredentialsTokenFactory
{
    Task<TokenResult> CreateAsync(
        ClientCredentialsRequest request,
        CancellationToken ct = default);
}

public record ClientCredentialsRequest(
    string ClientId,
    string[] RequestedScopes,
    string? DPoPProof,
    string TokenEndpointUrl);
```

---

#### IDeviceCodeTokenFactory

**Location**: `MrWhoOidc.Auth/Services/Token/IDeviceCodeTokenFactory.cs`

**Purpose**: Handles device code grant type polling from the token endpoint.

```csharp
namespace MrWhoOidc.Auth.Services.Token;

public interface IDeviceCodeTokenFactory
{
    Task<TokenResult> ProcessPollAsync(
        DeviceCodePollRequest request,
        CancellationToken ct = default);
}

public record DeviceCodePollRequest(
    string ClientId,
    string DeviceCode,
    string? DPoPProof,
    string TokenEndpointUrl);
```

---

### Authorization Processing Services

#### IAuthorizeRequestValidator

**Location**: `MrWhoOidc.Auth/Services/Authorization/IAuthorizeRequestValidator.cs`

**Purpose**: Validates OAuth 2.0 / OIDC authorization request parameters.

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

public interface IAuthorizeRequestValidator
{
    Task<AuthorizeValidationResult> ValidateAsync(
        AuthorizeRequest request,
        CancellationToken ct = default);
}

public record AuthorizeRequest(
    string ClientId,
    string? RedirectUri,
    string ResponseType,
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

public record AuthorizeValidationResult(
    bool IsValid,
    OAuthError? Error,
    ClientEntity? Client,
    Uri? ValidatedRedirectUri,
    string[] ValidatedScopes,
    string? RequestedAcr);
```

---

#### IConsentProcessor

**Location**: `MrWhoOidc.Auth/Services/Authorization/IConsentProcessor.cs`

**Purpose**: Determines and processes user consent for authorization requests.

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

public interface IConsentProcessor
{
    Task<ConsentDecision> EvaluateAsync(
        Guid userId,
        string clientId,
        string[] requestedScopes,
        CancellationToken ct = default);
    
    Task GrantAsync(
        Guid userId,
        string clientId,
        string[] grantedScopes,
        CancellationToken ct = default);
    
    Task RevokeAsync(
        Guid userId,
        string clientId,
        CancellationToken ct = default);
}

public record ConsentDecision(
    bool RequiresConsent,
    string[] PreviouslyGrantedScopes,
    string[] NewScopesRequiringConsent);
```

---

#### IProviderSelectionService

**Location**: `MrWhoOidc.Auth/Services/Authorization/IProviderSelectionService.cs`

**Purpose**: Handles provider selection logic for multi-provider scenarios.

```csharp
namespace MrWhoOidc.Auth.Services.Authorization;

public interface IProviderSelectionService
{
    Task<ProviderSelectionResult> DetermineProviderAsync(
        string clientId,
        string? idpHint,
        string? acrValues,
        CancellationToken ct = default);
}

public record ProviderSelectionResult(
    bool RequiresSelection,
    string? SelectedProviderId,
    IReadOnlyList<ProviderOption> AvailableProviders);

public record ProviderOption(
    string ProviderId,
    string DisplayName,
    string? IconUrl);
```

---

### Client Authentication Services

#### IClientAuthenticationService

**Location**: `MrWhoOidc.Auth/Services/Authentication/IClientAuthenticationService.cs`

**Purpose**: Pure domain logic for validating client credentials (no HTTP dependency).

```csharp
namespace MrWhoOidc.Auth.Services.Authentication;

public interface IClientAuthenticationService
{
    Task<ClientAuthResult> ValidateCredentialsAsync(
        ClientCredentialInput input,
        CancellationToken ct = default);
}

public record ClientCredentialInput(
    string ClientId,
    string? ClientSecret,
    string? ClientAssertion,
    string? ClientAssertionType,
    string AudienceUrl);

public record ClientAuthResult(
    bool IsAuthenticated,
    string? Error,
    string? ErrorDescription,
    ClientEntity? Client,
    string AuthenticationMethod);
```

---

### Key Management Services

#### ICachedKeyProvider

**Location**: `MrWhoOidc.Auth/Services/KeyManagement/ICachedKeyProvider.cs`

**Purpose**: Provides cached access to signing keys to eliminate blocking calls.

```csharp
namespace MrWhoOidc.Auth.Services.KeyManagement;

public interface ICachedKeyProvider
{
    Task<JsonWebKey> GetActiveSigningKeyAsync(CancellationToken ct = default);
    
    Task<IReadOnlyList<JsonWebKey>> GetValidationKeysAsync(CancellationToken ct = default);
    
    void InvalidateCache();
}
```

---

## Moved Types

### OidcOptions

**From**: `MrWhoOidc.WebAuth/Handlers/OidcOptions.cs`  
**To**: `MrWhoOidc.Auth/Options/OidcOptions.cs`

**Namespace Change**: `MrWhoOidc.WebAuth.Handlers` → `MrWhoOidc.Auth.Options`

```csharp
namespace MrWhoOidc.Auth.Options;

public sealed class OidcOptions
{
    public const string SectionName = "OidcSettings";
    
    public required string Issuer { get; init; }
    public required string[] Audiences { get; init; }
    public string[]? ApiAudiences { get; init; }
    public string? AdminAudience { get; init; }
    public int AccessTokenLifetimeMinutes { get; init; } = 60;
    public int RefreshTokenLifetimeDays { get; init; } = 30;
    public int IdTokenLifetimeMinutes { get; init; } = 60;
    public int AuthorizationCodeLifetimeSeconds { get; init; } = 600;
    // ... other properties unchanged
}
```

---

## Renamed Types

| Old Name | New Name | Location |
|----------|----------|----------|
| `OidcMetrics` (Auth) | `GlobalAuthMetrics` | `MrWhoOidc.Auth/Telemetry/GlobalAuthMetrics.cs` |
| `OidcMetrics` (WebAuth) | `OidcEndpointMetrics` | `MrWhoOidc.WebAuth/Telemetry/OidcEndpointMetrics.cs` |

---

## Shared Result Types

### TokenResult

**Location**: `MrWhoOidc.Auth/Services/Token/TokenResult.cs` (existing, no change)

Used as the common return type for all token-related services. Contains either a successful token response or an error.

### OAuthError

**Location**: `MrWhoOidc.Auth/Protocol/OAuthError.cs` (existing, no change)

Standard OAuth 2.0 error codes used across all validation services.

---

## Service Registration

All new interfaces will be registered in DI during Phase 2 implementation:

```csharp
// In MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs
services.AddScoped<IAuthorizationCodeExchanger, AuthorizationCodeExchanger>();
services.AddScoped<IRefreshTokenExchanger, RefreshTokenExchanger>();
services.AddScoped<IClientCredentialsTokenFactory, ClientCredentialsTokenFactory>();
services.AddScoped<IDeviceCodeTokenFactory, DeviceCodeTokenFactory>();
services.AddScoped<IAuthorizeRequestValidator, AuthorizeRequestValidator>();
services.AddScoped<IConsentProcessor, ConsentProcessor>();
services.AddScoped<IProviderSelectionService, ProviderSelectionService>();
services.AddScoped<IClientAuthenticationService, ClientAuthenticationService>();
services.AddSingleton<ICachedKeyProvider, CachedKeyProvider>();
```

---

## No Schema Changes

This refactoring does not introduce any new database entities or modify existing ones. All changes are purely at the code organization level.

Existing entities used by new services:
- `ClientEntity`
- `ConsentEntity`
- `AuthorizationCodeEntity`
- `RefreshTokenEntity`
- `AccessTokenEntity`
- `DeviceCodeEntity`
