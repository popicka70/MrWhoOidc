# MrWhoOidc Architecture Assessment & Refactoring Plan

**Date**: December 2025  
**Scope**: MrWhoOidc.Auth and MrWhoOidc.WebAuth projects  
**Goal**: Clean separation of concerns, eliminate god classes, reduce duplication, address security concerns, and fix flawed logic

---

## Executive Summary

This document provides a detailed assessment of the current architecture split between `MrWhoOidc.Auth` (OIDC engine) and `MrWhoOidc.WebAuth` (UI/HTTP surface) and prescribes a phased refactoring plan.

### Key Findings

| Category | Issues Found | Severity |
|----------|--------------|----------|
| Layer Violations | 8 | High |
| God Classes | 3 | High |
| Duplicated Code | 6 | Medium |
| Security Concerns | 5 | High |
| Flawed Logic | 4 | Medium |

---

## Refactoring Status (December 2025)

The refactoring plan outlined in this document has been **fully implemented** as part of the `015-auth-architecture-cleanup` feature.

### Completed Phases

1.  **Phase 1: Security Fixes**
    *   Fixed DPoP JKT confirmation in `TokenService`.
    *   Fixed `auth_time` fallback logic in `AuthorizeHandler`.
    *   Implemented strict `response_mode` validation.
2.  **Phase 2: Layer Violations**
    *   Moved `AuthorizeValidationResult` and `AuthorizeRequest` to `MrWhoOidc.Auth`.
    *   Moved `AuthorizeRequestResolver` to `MrWhoOidc.Auth`.
    *   Moved `JarmService` to `MrWhoOidc.Auth`.
    *   Moved `MtlsThumbprintResolver` to `MrWhoOidc.Auth`.
3.  **Phase 3: God Class Decomposition (TokenService)**
    *   Decomposed `TokenService` into `AuthorizationCodeExchanger`, `RefreshTokenExchanger`, and `ClientCredentialsTokenFactory`.
    *   Extracted `AccessTokenClaimBuilder` and `TokenLifetimeResolver`.
4.  **Phase 4: God Class Decomposition (AuthorizeHandler)**
    *   Decomposed `AuthorizeHandler` into `AuthorizeService`, `AuthorizeResponseGenerator`, and `ProviderSelector`.
    *   Extracted `AuthorizationCodeService` and `AuthorizationCodeMetadataStore`.
5.  **Phase 5: Duplication Removal**
    *   Consolidated token hash computation into `CryptoHelper`.
    *   Unified metrics into `OidcMetrics` and `GlobalAuthMetrics`.
    *   Consolidated role claim building into `RoleClaimBuilder`.
6.  **Phase 6: Polish & Cross-Cutting Concerns**
    *   Resolved all compiler warnings in core projects.
    *   Added XML documentation to all public interfaces in `MrWhoOidc.Auth`.
    *   Verified nullable reference type annotations.

### Final State
*   **Zero Warnings**: The solution builds with 0 warnings in `Auth` and `WebAuth`.
*   **Tests**: All 772 tests pass (769 successful, 3 skipped).
*   **Architecture**: Clean separation between protocol logic (`Auth`) and HTTP/UI surface (`WebAuth`). No layer violations remain.

---

## Part 1: Architecture Assessment

### 1.1 Current Layer Responsibilities

#### MrWhoOidc.Auth (Core Engine) - Current State

| Responsibility | Status | Notes |
|----------------|--------|-------|
| Token generation/validation | ✅ Correct | `TokenService`, `JwtService`, `TokenValidator` |
| Cryptography | ✅ Correct | `CryptoHelper`, `PasswordHasher`, `KeyStore` |
| Persistence | ✅ Correct | `AuthDbContext`, migrations |
| OIDC protocol constants | ✅ Correct | `OidcConstants`, `OAuthConstants`, `SecurityConstants` |
| Client store/validation | ⚠️ Mixed | Secret validation is correct; some HTTP concerns leak in |
| User management | ✅ Correct | `UserService`, `UserAccountService` |
| Multi-tenancy core | ✅ Correct | `TenantAccessor`, `TenantResolver` |
| Licensing/Entitlements | ✅ Correct | Properly isolated |

#### MrWhoOidc.WebAuth (UI/HTTP Surface) - Current State

| Responsibility | Status | Notes |
|----------------|--------|-------|
| HTTP endpoint handlers | ✅ Correct | `TokenHandler`, `AuthorizeHandler`, etc. |
| Razor Pages UI | ✅ Correct | Login, consent, admin pages |
| Admin API endpoints | ✅ Correct | REST APIs under `/admin` |
| Rate limiting | ✅ Correct | Infrastructure layer |
| DPoP HTTP validation | ✅ Correct | `DpopValidationHelper` |
| Background workers | ⚠️ Mixed | Some belong in Auth |
| Client authentication | ⚠️ Mixed | HTTP-specific but tightly coupled |
| Observability/Metrics | ⚠️ Duplicated | Two `OidcMetrics` classes |

---

### 1.2 Layer Violations Identified

#### LV-1: `ClientAuthenticator` in WebAuth uses domain logic

**Location**: `MrWhoOidc.WebAuth/Services/ClientAuthenticator.cs`  
**Issue**: Contains client secret validation logic that should be in Auth  
**Impact**: Duplicates validation logic, harder to test in isolation

```csharp
// Current: WebAuth contains authentication logic
public class ClientAuthenticator : IClientAuthenticator
{
    // Lines 115-175: Complex auth method selection logic
    // This is domain logic, not HTTP handling
}
```

**Recommendation**: Extract `IClientAuthenticationStrategy` interface to Auth, keep only HTTP parameter extraction in WebAuth.

---

#### LV-2: `RegistrationService` in WebAuth contains domain logic

**Location**: `MrWhoOidc.WebAuth/Services/RegistrationService.cs`  
**Issue**: User registration workflow belongs in Auth layer  
**Impact**: Cannot reuse registration logic in non-HTTP contexts (e.g., CLI tools, imports)

---

#### LV-3: `OidcOptions` defined in WebAuth but needed by Auth services

**Location**: `MrWhoOidc.WebAuth/Handlers/OidcOptions.cs`  
**Issue**: Auth services need issuer/audience configuration but it's in WebAuth  
**Impact**: Circular dependency risk, Auth services depend on WebAuth types

---

#### LV-4: `ImpersonationService` in WebAuth directly accesses `AuthDbContext`

**Location**: `MrWhoOidc.WebAuth/Services/ImpersonationService.cs`  
**Issue**: Writes audit logs directly to DB instead of through Auth service  
**Impact**: Bypasses domain layer, audit logic scattered

---

#### LV-5: Token grant handlers in WebAuth contain business logic

**Location**: `MrWhoOidc.WebAuth/TokenEndpoint/Grants/`  
**Issue**: Grant handlers like `AuthorizationCodeGrantHandler` should only be thin HTTP adapters  
**Impact**: Business logic mixed with HTTP concerns

---

#### LV-6: `LogoutTokenBuilder` in WebAuth contains JWT creation

**Location**: `MrWhoOidc.WebAuth/Handlers/Logout/LogoutTokenBuilder.cs`  
**Issue**: JWT token creation belongs in Auth's `JwtService`  
**Impact**: Token creation scattered across layers

---

#### LV-7: `BackchannelLogoutDispatcher` uses HTTP directly

**Location**: `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs`  
**Issue**: Background service that sends HTTP requests - belongs in Auth with HTTP abstraction  
**Impact**: Cannot test backchannel logic without HTTP mocking

---

#### LV-8: Discovery handler reads DB directly

**Location**: `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`  
**Issue**: Queries `AuthDbContext` for scopes instead of using service  
**Impact**: Bypasses caching, breaks single responsibility

---

### 1.3 God Classes Identified

#### GC-1: `TokenService` (723 lines)

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs`  
**Responsibilities identified**:
1. Authorization code exchange (lines 30-320)
2. Refresh token exchange (lines 322-485)
3. Client credentials token creation (lines 487-580)
4. Opaque token persistence (lines 640-665)
5. Product entitlements processing (lines 582-638)
6. Role/realm claim computation (interleaved throughout)
7. DPoP confirmation claim generation (interleaved)
8. Tenants claim generation (interleaved)

**Recommended split**:
- `AuthorizationCodeExchanger`
- `RefreshTokenExchanger`
- `ClientCredentialsTokenFactory`
- `AccessTokenClaimBuilder`
- `OpaqueTokenStore`

---

#### GC-2: `AuthorizeHandler` (708 lines)

**Location**: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`  
**Responsibilities identified**:
1. Parameter parsing and sanitization
2. PAR/JAR resolution
3. Client validation
4. Provider selection logic
5. QR login flow initiation
6. Consent checking
7. Authorization code issuance
8. JARM response generation
9. Cookie management for last IdP

**Recommended split**:
- `AuthorizeRequestParser`
- `ProviderSelector`
- `ConsentOrchestrator`
- `AuthorizationCodeIssuer`
- `JarmResponseBuilder`

---

#### GC-3: `AuthDbContext` (1772 lines)

**Location**: `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`  
**Issue**: Contains 50+ DbSets, custom save logic, and entity configurations inline  
**Recommended split**:
- Move entity configurations to separate `IEntityTypeConfiguration<T>` classes (partially done under `Configurations/`)
- Extract `SaveChangesAsync` interception to separate interceptor
- Consider splitting into bounded context DbContexts for large deployments

---

### 1.4 Duplicated Code Identified

#### DC-1: Hash computation methods

**Locations**:
- `MrWhoOidc.Auth/Utils/CryptoHelper.cs` (consolidated ✅)
- `MrWhoOidc.Auth/Services/TokenService.cs` (legacy helpers at bottom)

```csharp
// TokenService.cs lines 667-669 - delegate to CryptoHelper
static string ComputeS256(string verifier) => CryptoHelper.ComputePkceS256(verifier);
static string ComputeAtHash(string accessToken) => CryptoHelper.ComputeLeftHalfSha256Base64Url(accessToken);
static string Hash(string value) => CryptoHelper.ComputeSha256Base64(value);
```

**Action**: Remove legacy wrappers, use `CryptoHelper` directly.

---

#### DC-2: Duplicated `OidcMetrics` classes

**Locations**:
- `MrWhoOidc.Auth/Observability/OidcMetrics.cs` (global auth metrics)
- `MrWhoOidc.WebAuth/Observability/OidcMetrics.cs` (HTTP endpoint metrics)

**Issue**: Same class name, different meters, confusing  
**Action**: Rename Auth version to `GlobalAuthMetrics`, keep WebAuth as `OidcEndpointMetrics`.

---

#### DC-3: Tenant settings loading repeated across services

**Locations**:
- `TokenService.cs` - loads settings for token lifetime
- `TokenExchangeService.cs` - loads settings for token lifetime
- `AuthorizeHandler.cs` - loads client settings

**Action**: Create `ITokenLifetimeResolver` that encapsulates tenant → client → default cascade.

---

#### DC-4: Role claim building logic duplicated

**Locations**:
- `TokenService.ExchangeAuthorizationCodeAsync` lines 106-118
- `TokenService.ExchangeRefreshTokenAsync` lines 378-394
- `TokenExchangeService.ExchangeTokenAsync` (similar pattern)

**Action**: Extract `IRoleClaimBuilder` interface in Auth.

---

#### DC-5: Opaque token detection/validation pattern

**Locations**:
- `TokenService.cs` - checks `OpaqueAccessTokens.Enabled`
- `TokenExchangeService.cs` - duplicates same check
- `TokenValidator.cs` - different path

**Action**: Create `IOpaqueTokenPolicy` to centralize opaque token decisions.

---

#### DC-6: Client mTLS thumbprint parsing

**Locations**:
- `ClientAuthenticator.GetAllowedMtlsThumbprints` 
- Similar patterns in introspection handlers

**Action**: Extract to `IMtlsThumbprintResolver` in Auth.

---

### 1.5 Security Concerns Identified

#### SC-1: Blocking call in JWT creation

**Location**: `MrWhoOidc.Auth/Services/JwtService.cs` line 30

```csharp
var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
```

**Issue**: Synchronous blocking call in async context can cause threadpool starvation  
**Risk**: Medium - DoS vector under high load  
**Action**: Make `CreateJwt` async or cache the key with short TTL.

---

#### SC-2: Missing input validation on `subject_token`

**Location**: `MrWhoOidc.Auth/Services/TokenExchangeService.cs`  
**Issue**: While nullity is checked, no maximum length validation exists  
**Risk**: Low - potential for oversized token processing  
**Action**: Add `MaxSubjectTokenLength` configuration.

---

#### SC-3: Legacy client secret hash still supported

**Location**: `MrWhoOidc.Auth/Services/ClientStore.cs` lines 132-140

```csharp
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility
// Fall back to legacy single secret for backward compatibility
```

**Risk**: Medium - old secrets may use weaker hashing  
**Action**: Add deprecation timeline, emit metrics when legacy path used.

---

#### SC-4: No explicit token size limit in PAR/JAR

**Location**: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`  
**Current**: Only `RequestObjectMaxBytes` is checked (line 199-204)  
**Issue**: PAR requests via `request_uri` don't have size limit until fetch  
**Action**: Add `ParResponseMaxBytes` limit, validate before processing.

---

#### SC-5: Impersonation without time limit

**Location**: `MrWhoOidc.WebAuth/Services/ImpersonationService.cs`  
**Issue**: Session-based impersonation has no automatic expiry  
**Risk**: Medium - forgotten impersonation session  
**Action**: Add `MaxImpersonationDurationMinutes` with auto-revoke.

---

### 1.6 Flawed Logic Identified

#### FL-1: Inconsistent audience validation in token exchange

**Location**: `MrWhoOidc.Auth/Services/TokenExchangeService.cs` lines 95-104  
**Issue**: For JWT subject tokens, audience validation happens after parsing; for opaque, it's skipped  
**Impact**: Opaque tokens may be exchanged for any audience

```csharp
// JWT path validates audience
if (!string.IsNullOrEmpty(sourceAudience) && allowedAudiences.Length > 0 && 
    !allowedAudiences.Contains(sourceAudience, StringComparer.Ordinal))
// Opaque path skips this check
```

**Action**: Apply same audience validation to opaque tokens using stored `Audience` field.

---

#### FL-2: Race condition in consent grant

**Location**: `MrWhoOidc.Auth/Services/ConsentService.cs` lines 47-73  
**Issue**: Read-modify-write without transaction on consent merge

```csharp
var existing = await query.FirstOrDefaultAsync(ct);
// ... time passes ...
existing.ScopesJson = System.Text.Json.JsonSerializer.Serialize(merged);
await db.SaveChangesAsync(ct);
```

**Action**: Use `ExecutionStrategy` with transaction like `TokenService` does.

---

#### FL-3: Missing null check in role query

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs` line 97  
**Issue**: If client has no realm, `client.RealmId` could be empty GUID causing empty role results

```csharp
.Where(a => a.UserId == entity.UserId && a.RealmId == client.RealmId && a.IsActive)
```

**Action**: Skip realm role query when `client.RealmId == Guid.Empty`.

---

#### FL-4: Tenants claim built multiple times

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs`  
**Issue**: `tenantsClaimService.BuildTenantsClaimJsonAsync` called for both access and ID token if scope present  
**Impact**: Performance - redundant DB queries

**Action**: Cache result in local variable, reuse for both tokens.

---

## Part 2: Refactoring Plan

### Phase 1: Critical Security Fixes (Week 1)

| ID | Task | File(s) | Priority |
|----|------|---------|----------|
| SC-1 | Make key loading async in JwtService | `JwtService.cs` | P0 |
| FL-1 | Add audience validation for opaque token exchange | `TokenExchangeService.cs` | P0 |
| FL-2 | Add transaction to consent grant | `ConsentService.cs` | P1 |
| SC-3 | Add metrics for legacy secret usage | `ClientStore.cs` | P1 |

### Phase 2: Layer Violation Fixes (Weeks 2-3)

| ID | Task | Source | Target | Priority |
|----|------|--------|--------|----------|
| LV-1 | Extract client auth strategy | `WebAuth/Services/ClientAuthenticator.cs` | `Auth/Services/ClientAuthenticationService.cs` | P1 |
| LV-2 | Move registration domain logic | `WebAuth/Services/RegistrationService.cs` | `Auth/Services/RegistrationService.cs` | P1 |
| LV-3 | Move `OidcOptions` to Auth | `WebAuth/Handlers/OidcOptions.cs` | `Auth/Options/OidcOptions.cs` | P2 |
| LV-6 | Move logout token creation | `WebAuth/Handlers/Logout/LogoutTokenBuilder.cs` | `Auth/Services/LogoutTokenService.cs` | P2 |

### Phase 3: God Class Decomposition (Weeks 4-6)

#### TokenService Decomposition

```
TokenService (current: 723 lines)
├── IAccessTokenBuilder          - Build claims for access tokens
│   ├── AccessTokenClaimBuilder.cs (new)
│   └── ClaimBuilderOptions.cs (new)
├── IAuthorizationCodeExchanger  - Handle auth code → token
│   └── AuthorizationCodeExchanger.cs (new)
├── IRefreshTokenExchanger       - Handle refresh → token
│   └── RefreshTokenExchanger.cs (new)
├── IClientCredentialsTokenFactory - Handle M2M tokens
│   └── ClientCredentialsTokenFactory.cs (new)
└── TokenService.cs              - Orchestrator (target: <150 lines)
```

#### AuthorizeHandler Decomposition

```
AuthorizeHandler (current: 708 lines)
├── IAuthorizeRequestParser      - Parse & sanitize params
│   └── AuthorizeRequestParser.cs (move to Auth)
├── IProviderSelector            - External IdP selection
│   └── ProviderSelector.cs (new in WebAuth)
├── IAuthorizationCodeIssuer     - Issue codes
│   └── AuthorizationCodeIssuer.cs (new in Auth)
└── AuthorizeHandler.cs          - HTTP orchestrator (target: <200 lines)
```

### Phase 4: Duplication Removal (Week 7)

| ID | Task | Action |
|----|------|--------|
| DC-1 | Remove legacy crypto wrappers | Delete helper methods in `TokenService.cs`, update callers |
| DC-2 | Rename duplicate metrics class | `Auth/.../OidcMetrics.cs` → `GlobalAuthMetrics.cs` |
| DC-3 | Create token lifetime resolver | New `Auth/Services/TokenLifetimeResolver.cs` |
| DC-4 | Create role claim builder | New `Auth/Services/RoleClaimBuilder.cs` |
| DC-5 | Create opaque token policy | New `Auth/Services/OpaqueTokenPolicy.cs` |
| DC-6 | Create mTLS resolver | New `Auth/Services/MtlsThumbprintResolver.cs` |

### Phase 5: Code Quality (Week 8)

| Task | Files | Notes |
|------|-------|-------|
| Add XML documentation | All public interfaces in Auth | Required for API docs |
| Add nullability annotations | Services in Auth | C# nullable reference types |
| Extract DB configurations | `AuthDbContext.cs` | Move remaining inline configs |
| Add integration tests | New test files | Cover refactored services |

---

## Part 3: Target Architecture

### 3.1 MrWhoOidc.Auth - Final Structure

```
MrWhoOidc.Auth/
├── Crypto/
│   ├── EcJwk.cs
│   ├── RsaJwk.cs
│   └── KeyStore.cs (move from Services)
├── Options/
│   ├── OidcOptions.cs (move from WebAuth)
│   ├── AuthOptions.cs
│   └── TokenLifetimeOptions.cs (new)
├── Protocols/
│   ├── OAuthConstants.cs
│   ├── OidcConstants.cs
│   └── SecurityConstants.cs
├── Services/
│   ├── Authentication/
│   │   ├── IClientAuthenticationService.cs (new)
│   │   ├── ClientSecretAuthenticator.cs (new)
│   │   └── PrivateKeyJwtAuthenticator.cs (new)
│   ├── Authorization/
│   │   ├── IAuthorizeService.cs
│   │   ├── AuthorizeService.cs
│   │   └── ConsentService.cs
│   ├── Tokens/
│   │   ├── ITokenService.cs (simplified)
│   │   ├── AuthorizationCodeExchanger.cs (new)
│   │   ├── RefreshTokenExchanger.cs (new)
│   │   ├── ClientCredentialsTokenFactory.cs (new)
│   │   ├── AccessTokenClaimBuilder.cs (new)
│   │   └── TokenValidator.cs
│   ├── Users/
│   │   ├── IUserService.cs
│   │   ├── UserService.cs
│   │   ├── IRegistrationService.cs (move from WebAuth)
│   │   └── RegistrationService.cs (move from WebAuth)
│   └── ... (other existing services)
├── Utils/
│   ├── CryptoHelper.cs
│   ├── UrlComparison.cs
│   └── Bucketization.cs
└── Observability/
    └── GlobalAuthMetrics.cs (rename)
```

### 3.2 MrWhoOidc.WebAuth - Final Structure

```
MrWhoOidc.WebAuth/
├── Handlers/
│   ├── AuthorizeHandler.cs (<200 lines)
│   ├── TokenHandler.cs (unchanged - already thin)
│   ├── DiscoveryHandler.cs (use IDiscoveryService)
│   └── ... (other handlers)
├── Services/
│   ├── IClientAuthenticator.cs (HTTP parameter extraction only)
│   ├── ClientAuthenticator.cs (delegates to Auth)
│   └── ... (UI-specific services)
├── TokenEndpoint/
│   └── Grants/ (thin HTTP adapters)
├── Observability/
│   ├── OidcEndpointMetrics.cs (rename from OidcMetrics)
│   └── ... (other observability)
└── ... (Pages, Admin, etc. unchanged)
```

---

## Part 4: Migration Guide

### 4.1 Breaking Changes

1. **`ITokenService` interface change** - Methods will return strongly-typed results instead of tuples
2. **`OidcOptions` namespace change** - From `MrWhoOidc.WebAuth.Handlers` to `MrWhoOidc.Auth.Options`
3. **`OidcMetrics` class rename** - Auth's version becomes `GlobalAuthMetrics`

### 4.2 Deprecated APIs

| Current | Replacement | Removal Version |
|---------|-------------|-----------------|
| `Client.ClientSecretHash` | `ClientSecrets` collection | 2.0 |
| `TokenService` legacy hash helpers | `CryptoHelper` | Next release |

### 4.3 Testing Strategy

1. **Unit tests**: Each new service class gets dedicated test class
2. **Integration tests**: Existing `MrWhoOidc.UnitTests` tests should pass without modification
3. **Snapshot tests**: Keep existing snapshot tests for API surface stability

---

## Part 5: Implementation Checklist

### Pre-Refactoring

- [ ] Create feature branch `refactor/architecture-cleanup`
- [ ] Run full test suite, record baseline
- [ ] Document current public API surface

### Phase 1 (Security)

- [ ] SC-1: Fix blocking call in JwtService
- [ ] FL-1: Add audience validation for opaque tokens
- [ ] FL-2: Add transaction to consent
- [ ] Run security-focused tests

### Phase 2 (Layer Violations)

- [ ] LV-1: Extract client auth strategy
- [ ] LV-2: Move registration to Auth
- [ ] LV-3: Move OidcOptions
- [ ] LV-6: Move logout token builder
- [ ] Update DI registrations
- [ ] Run all tests

### Phase 3 (God Classes)

- [ ] Decompose TokenService
- [ ] Decompose AuthorizeHandler
- [ ] Extract DbContext configurations
- [ ] Run all tests

### Phase 4 (Duplication)

- [ ] DC-1 through DC-6
- [ ] Run all tests

### Phase 5 (Quality)

- [ ] Add XML docs
- [ ] Add nullability
- [ ] Add new integration tests
- [ ] Final test run

### Post-Refactoring

- [ ] Update `copilot-instructions.md` with new architecture
- [ ] Update `developer-guide.md`
- [ ] PR review with focus on breaking changes
- [ ] Version bump consideration

---

## Appendix A: File Inventory

### Files to Move (Auth ← WebAuth)

| File | New Location |
|------|--------------|
| `WebAuth/Handlers/OidcOptions.cs` | `Auth/Options/OidcOptions.cs` |
| `WebAuth/Services/RegistrationService.cs` (domain parts) | `Auth/Services/Users/RegistrationService.cs` |
| `WebAuth/Handlers/Logout/LogoutTokenBuilder.cs` (JWT creation) | `Auth/Services/Tokens/LogoutTokenService.cs` |

### Files to Rename

| Current | New Name |
|---------|----------|
| `Auth/Observability/OidcMetrics.cs` | `Auth/Observability/GlobalAuthMetrics.cs` |

### Files to Create

| Path | Purpose |
|------|---------|
| `Auth/Services/Authentication/IClientAuthenticationService.cs` | Client auth abstraction |
| `Auth/Services/Tokens/AuthorizationCodeExchanger.cs` | Auth code exchange logic |
| `Auth/Services/Tokens/RefreshTokenExchanger.cs` | Refresh token exchange logic |
| `Auth/Services/Tokens/ClientCredentialsTokenFactory.cs` | M2M token creation |
| `Auth/Services/Tokens/AccessTokenClaimBuilder.cs` | Claim building logic |
| `Auth/Services/TokenLifetimeResolver.cs` | Centralized lifetime resolution |
| `Auth/Services/RoleClaimBuilder.cs` | Role claim construction |
| `Auth/Services/OpaqueTokenPolicy.cs` | Opaque token decisions |
| `Auth/Services/MtlsThumbprintResolver.cs` | mTLS thumbprint lookup |

---

## Appendix B: Risk Assessment

| Change | Risk | Mitigation |
|--------|------|------------|
| TokenService decomposition | High - critical path | Extensive test coverage first |
| OidcOptions move | Medium - namespace change | Automated refactoring tool |
| Metrics rename | Low - internal only | Grep + replace |
| Interface changes | High - breaking | Version bump, deprecation period |

---

*Document prepared by architectural assessment on 2025-12-27*
