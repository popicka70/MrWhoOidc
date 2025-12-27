# Quickstart: Auth Architecture Cleanup

**Feature**: 015-auth-architecture-cleanup  
**For**: Developers implementing the refactoring

## Overview

This guide helps you start implementing the auth architecture cleanup. The refactoring is organized into 5 phases that can be partially parallelized.

---

## Prerequisites

- .NET 9 SDK
- Visual Studio 2022 or VS Code with C# Dev Kit
- PostgreSQL (via Aspire/Docker)
- Familiarity with the existing `MrWhoOidc.Auth` and `MrWhoOidc.WebAuth` projects

---

## Quick Reference

| Document | Purpose |
|----------|---------|
| [spec.md](spec.md) | Feature requirements and success criteria |
| [plan.md](plan.md) | Implementation plan and phases |
| [research.md](research.md) | Technical decisions and patterns |
| [data-model.md](data-model.md) | New interfaces and types |
| [contracts/](contracts/) | Detailed API contracts per domain |
| [architecture-refactoring-plan.md](../../docs/architecture-refactoring-plan.md) | Original assessment |

---

## Phase Implementation Order

```
Phase 1: Security Fixes (P1) ─────────────────────────────┐
                                                          │
Phase 2: Layer Violations (P2) ──────────────────────────┼── Can start after Phase 1
                                                          │
Phase 3: God Class Decomposition (P3) ───────────────────┘
                                                          
Phase 4: Duplication Removal (P4) ── After Phase 3 complete

Phase 5: Cleanup (P5) ── Final phase
```

---

## Phase 1: Security Fixes (Start Here)

**Files to modify**:
- `MrWhoOidc.Auth/Services/JwtService.cs`
- `MrWhoOidc.Auth/Services/ConsentService.cs`
- `MrWhoOidc.Auth/Services/TokenExchangeService.cs`

### Task 1.1: Fix Blocking Async in JwtService

1. Create `MrWhoOidc.Auth/Services/KeyManagement/ICachedKeyProvider.cs`
2. Create `MrWhoOidc.Auth/Services/KeyManagement/CachedKeyProvider.cs`
3. Update `JwtService` constructor to use `ICachedKeyProvider`
4. Register in DI as singleton

**Pattern** (from [research.md](research.md#r1-async-key-loading-pattern-for-jwtservice)):
```csharp
// In JwtService - key is now cached, no blocking I/O
var signingKey = keyProvider.GetActiveSigningKeyAsync()
    .GetAwaiter().GetResult(); // Safe: cache hit
```

### Task 1.2: Fix Race Condition in ConsentService

1. Locate `GrantConsentAsync` in `ConsentService.cs`
2. Wrap the read-modify-write in a transaction

**Pattern** (from [research.md](research.md#r2-transaction-pattern-for-consent-service)):
```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await db.Database.BeginTransactionAsync(ct);
    // ... existing logic ...
    await transaction.CommitAsync(ct);
});
```

### Task 1.3: Add Audience Validation for Opaque Tokens

1. Locate token exchange opaque token handling in `TokenExchangeService.cs`
2. Add audience check after loading token entity

**Pattern** (from [research.md](research.md#r7-audience-validation-for-opaque-tokens)):
```csharp
var allowedAudiences = authOptions.Value.ApiAudiences ?? [];
if (!string.IsNullOrEmpty(entity.Audience) && allowedAudiences.Length > 0 
    && !allowedAudiences.Contains(entity.Audience, StringComparer.Ordinal))
{
    return InvalidGrant();
}
```

---

## Phase 2: Layer Violations

**Files to move/refactor**:
- `OidcOptions` → `MrWhoOidc.Auth/Options/`
- `OidcMetrics` → Rename appropriately
- `ClientAuthenticator` → Split between layers

### Task 2.1: Move OidcOptions

1. Create `MrWhoOidc.Auth/Options/OidcOptions.cs` (copy content)
2. Update namespace to `MrWhoOidc.Auth.Options`
3. Find all usages in WebAuth:
   ```powershell
   grep -r "using.*WebAuth.*OidcOptions" .
   ```
4. Update imports
5. Delete old file

### Task 2.2: Rename Metrics Classes

1. `MrWhoOidc.Auth/Telemetry/OidcMetrics.cs` → `GlobalAuthMetrics.cs`
2. `MrWhoOidc.WebAuth/Telemetry/OidcMetrics.cs` → `OidcEndpointMetrics.cs`
3. Update all references

### Task 2.3: Split ClientAuthenticator

1. Create `MrWhoOidc.Auth/Services/Authentication/IClientAuthenticationService.cs`
2. Create `MrWhoOidc.Auth/Services/Authentication/ClientAuthenticationService.cs`
3. Refactor `ClientAuthenticator` in WebAuth to delegate

See [contracts/client-authentication.md](contracts/client-authentication.md) for full interface.

---

## Phase 3: God Class Decomposition

**Large files to decompose**:
- `TokenService.cs` (723 lines)
- `AuthorizeHandler.cs` (708 lines)

### Task 3.1: Decompose TokenService

Create in `MrWhoOidc.Auth/Services/Token/`:
- `IAuthorizationCodeExchanger.cs` + `AuthorizationCodeExchanger.cs`
- `IRefreshTokenExchanger.cs` + `RefreshTokenExchanger.cs`
- `IClientCredentialsTokenFactory.cs` + `ClientCredentialsTokenFactory.cs`
- `IDeviceCodeTokenFactory.cs` + `DeviceCodeTokenFactory.cs`

Then update `TokenService` to delegate:
```csharp
public class TokenService(
    IAuthorizationCodeExchanger codeExchanger,
    IRefreshTokenExchanger refreshExchanger,
    ...) : ITokenService
{
    public Task<TokenResult> ExchangeAuthorizationCodeAsync(...) 
        => codeExchanger.ExchangeAsync(...);
}
```

### Task 3.2: Decompose AuthorizeHandler

Create in `MrWhoOidc.Auth/Services/Authorization/`:
- `IAuthorizeRequestValidator.cs` + `AuthorizeRequestValidator.cs`
- `IConsentProcessor.cs` + `ConsentProcessor.cs`
- `IProviderSelectionService.cs` + `ProviderSelectionService.cs`

See [contracts/authorization-processing.md](contracts/authorization-processing.md) for interfaces.

---

## Running Tests

After each phase:

```bash
# Run all tests
dotnet test

# Or run specific test project
dotnet test MrWhoOidc.UnitTests
```

Key test files to verify:
- `TokenServiceTests.cs` - token flows
- `ClientStoreTests.cs` - client operations  
- `ConsentTests.cs` - consent logic

---

## Common Gotchas

1. **Circular Dependencies**: If Auth references WebAuth, you have a layer violation
2. **Missing DI Registration**: New services must be registered in `ServiceCollectionExtensions.cs`
3. **Transaction Scope**: EF Core `CreateExecutionStrategy` required for retry-safe transactions
4. **TimeProvider**: Always inject `TimeProvider` instead of using `DateTime.UtcNow`

---

## Checklist per Service Extraction

- [ ] Interface created in correct namespace
- [ ] Implementation follows single responsibility
- [ ] Unit tests added/updated
- [ ] DI registration added
- [ ] Original class delegates to new service
- [ ] All usages compile
- [ ] `dotnet test` passes

---

## Getting Help

- Review existing similar services for patterns
- Check [constitution.md](../../docs/constitution.md) for architectural rules
- Tests in `MrWhoOidc.UnitTests` serve as examples
