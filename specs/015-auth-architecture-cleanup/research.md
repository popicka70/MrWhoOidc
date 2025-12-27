# Research: Auth Architecture Cleanup

**Feature**: 015-auth-architecture-cleanup  
**Date**: 2025-12-27  
**Status**: Complete

## Overview

This research document consolidates findings for the architectural refactoring of MrWhoOidc.Auth and MrWhoOidc.WebAuth. Since the feature is based on the detailed [architecture-refactoring-plan.md](../../docs/architecture-refactoring-plan.md), most technical decisions are already documented. This research focuses on implementation patterns and best practices.

## Research Tasks

### R1: Async Key Loading Pattern for JwtService

**Context**: Current `JwtService.CreateJwt` blocks on `GetActiveSigningKeyAsync().GetAwaiter().GetResult()` which can cause threadpool starvation under high load.

**Decision**: Implement key caching with short TTL rather than making CreateJwt fully async.

**Rationale**:
- Making `IJwtService.CreateJwt` async would require cascading changes to all callers
- Signing keys change infrequently (hours/days), making caching safe
- A 5-minute memory cache with async refresh on miss balances performance and key rotation responsiveness

**Alternatives Considered**:
| Alternative | Rejected Because |
|------------|------------------|
| Full async chain | Too many breaking changes to interface contracts |
| Pre-load key at startup | Doesn't handle key rotation gracefully |
| Lazy async initialization | Still blocks on first call, just defers the problem |

**Implementation Pattern**:
```csharp
// Cache active key with periodic async refresh
private JsonWebKey? _cachedKey;
private DateTime _cacheExpiry = DateTime.MinValue;
private readonly SemaphoreSlim _lock = new(1, 1);

private async Task<JsonWebKey> GetCachedKeyAsync()
{
    if (_cachedKey != null && DateTime.UtcNow < _cacheExpiry)
        return _cachedKey;
    
    await _lock.WaitAsync();
    try
    {
        if (_cachedKey != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedKey;
        
        var jwk = await keyStore.GetActiveSigningKeyAsync();
        _cachedKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
        _cacheExpiry = DateTime.UtcNow.AddMinutes(5);
        return _cachedKey;
    }
    finally
    {
        _lock.Release();
    }
}
```

---

### R2: Transaction Pattern for Consent Service

**Context**: Current `ConsentService.GrantConsentAsync` has read-modify-write without transaction, creating race condition risk.

**Decision**: Use EF Core's `CreateExecutionStrategy` with explicit transaction, matching the pattern already used in `TokenService`.

**Rationale**:
- Consistent with existing codebase patterns (TokenService.ExchangeAuthorizationCodeAsync)
- Handles PostgreSQL retry logic correctly
- Minimal code change with maximum safety improvement

**Implementation Pattern**:
```csharp
public async Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
{
    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // ... existing logic ...
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    });
}
```

---

### R3: Service Extraction Pattern for God Class Decomposition

**Context**: TokenService (723 lines) and AuthorizeHandler (708 lines) need to be broken into smaller, focused services.

**Decision**: Use the Facade + Strategy pattern where the original class becomes a thin orchestrator delegating to specialized services.

**Rationale**:
- Maintains backward compatibility (ITokenService interface unchanged)
- Allows incremental extraction (one method at a time)
- Each extracted service is independently testable
- DI registration stays simple (one ITokenService implementation)

**Alternatives Considered**:
| Alternative | Rejected Because |
|------------|------------------|
| Full interface redesign | Breaking change, high risk |
| Partial classes | Doesn't reduce complexity, just splits files |
| Inheritance hierarchy | Creates coupling, harder to test |

**Implementation Pattern**:
```csharp
// TokenService becomes orchestrator
internal sealed class TokenService(
    IAuthorizationCodeExchanger codeExchanger,
    IRefreshTokenExchanger refreshExchanger,
    IClientCredentialsTokenFactory credentialsFactory) : ITokenService
{
    public Task<TokenResult> ExchangeAuthorizationCodeAsync(...) 
        => codeExchanger.ExchangeAsync(...);
    
    public Task<TokenResult> ExchangeRefreshTokenAsync(...) 
        => refreshExchanger.ExchangeAsync(...);
    
    public Task<TokenResult> CreateClientCredentialsTokenAsync(...) 
        => credentialsFactory.CreateAsync(...);
}
```

---

### R4: Layer Separation Pattern for Client Authentication

**Context**: `ClientAuthenticator` in WebAuth contains both HTTP parameter extraction and credential validation logic.

**Decision**: Split into two parts:
1. `IClientAuthenticationService` in Auth (credential validation)
2. `ClientAuthenticator` in WebAuth (HTTP extraction, delegates to Auth)

**Rationale**:
- Follows Constitution §II (domain logic in Auth, HTTP in WebAuth)
- Enables unit testing of validation logic without HTTP mocking
- Auth service can be reused in non-HTTP contexts (CLI tools, imports)

**Implementation Pattern**:
```csharp
// In MrWhoOidc.Auth/Services/Authentication/
public interface IClientAuthenticationService
{
    Task<ClientAuthResult> ValidateCredentialsAsync(
        string clientId, 
        string? clientSecret, 
        string? clientAssertion,
        string? clientAssertionType,
        string audienceUrl,
        CancellationToken ct = default);
}

// In MrWhoOidc.WebAuth/Services/
public class ClientAuthenticator : IClientAuthenticator
{
    public async Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext http, ...)
    {
        // Extract from HTTP
        var (clientId, secret) = ExtractFromHeaders(http);
        var (assertion, assertionType) = ExtractFromForm(http);
        var audienceUrl = http.GetEndpointUrl();
        
        // Delegate to Auth
        var result = await authService.ValidateCredentialsAsync(
            clientId, secret, assertion, assertionType, audienceUrl);
        
        // Map to HTTP result
        return MapToHttpResult(result);
    }
}
```

---

### R5: Metrics Naming Strategy

**Context**: Two classes named `OidcMetrics` exist in Auth and WebAuth with different purposes.

**Decision**: 
- Auth: Rename to `GlobalAuthMetrics` (focuses on credential/authentication operations)
- WebAuth: Rename to `OidcEndpointMetrics` (focuses on HTTP endpoint metrics)

**Rationale**:
- Clear naming prevents confusion
- Each class has distinct meter name already (`MrWhoOidc.Auth.GlobalAuth` vs `MrWhoOidc.WebAuth`)
- Minimal code change (rename + update usages)

---

### R6: OidcOptions Location

**Context**: `OidcOptions` is in WebAuth but needed by Auth services for issuer/audience configuration.

**Decision**: Move to `MrWhoOidc.Auth/Options/OidcOptions.cs`.

**Rationale**:
- Removes circular dependency risk
- Configuration is domain concern, not HTTP concern
- WebAuth will reference Auth's OidcOptions via normal project reference

**Migration Steps**:
1. Create `MrWhoOidc.Auth/Options/OidcOptions.cs` with same content
2. Update namespace in all usages (`using MrWhoOidc.Auth.Options;`)
3. Delete `MrWhoOidc.WebAuth/Handlers/OidcOptions.cs`
4. No functional changes needed

---

### R7: Audience Validation for Opaque Tokens

**Context**: Token exchange validates audience for JWT subject tokens but skips validation for opaque tokens.

**Decision**: Add audience validation to opaque token path using stored `Audience` field.

**Rationale**:
- Parity with JWT path prevents security gap
- Opaque token entity already stores `Audience` field
- Simple conditional check addition

**Implementation Pattern**:
```csharp
// In TokenExchangeService after loading opaque token entity
if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= DateTimeOffset.UtcNow)
    return InvalidGrant();

// NEW: Audience validation for opaque tokens
var allowedAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
if (!string.IsNullOrEmpty(entity.Audience) && allowedAudiences.Length > 0 
    && !allowedAudiences.Contains(entity.Audience, StringComparer.Ordinal))
{
    return InvalidGrant();
}
```

---

## Dependency Analysis

### Internal Dependencies (within solution)

| Component | Depends On | Impact |
|-----------|------------|--------|
| TokenService decomposition | JwtService, RefreshTokenService, AuthDbContext | Must extract after services stable |
| AuthorizeHandler decomposition | ConsentService, AuthorizationCodeService, ClientStore | Can proceed in parallel with Token |
| OidcOptions move | All services using issuer/audience | Must be first to avoid import issues |
| Metrics rename | Logging/observability consumers | Low impact, search-replace |

### External Dependencies

No new external dependencies required. All patterns use existing .NET/EF Core capabilities.

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Regression in token flows | Medium | High | Comprehensive existing tests + new unit tests for extracted services |
| Performance degradation from additional service hops | Low | Medium | Measure baseline before/after, services are in-process calls |
| Merge conflicts with active development | Medium | Low | Complete phase-by-phase, merge frequently |
| Missed usages during namespace changes | Low | Low | IDE refactoring tools + grep verification |

---

## Conclusion

All technical decisions are resolved. No NEEDS CLARIFICATION items remain. The refactoring can proceed through the 5 phases as outlined in the spec and architecture plan.

**Next Steps**: Generate data-model.md and contracts (Phase 1)
