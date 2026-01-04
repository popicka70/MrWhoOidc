# Phase 2: TokenService Integration with Tenant-Scoped Scopes

**Date:** October 11, 2025  
**Status:** ✅ Complete

## Overview

Phase 2 integrated the `IScopeResolver` service into `TokenService` to add tenant context to access tokens containing custom (tenant-scoped) scopes. This ensures downstream APIs can validate that custom scopes belong to the correct tenant.

## Changes Made

### 1. TokenService Updates

**File:** `MrWhoOidc.Auth/Services/TokenService.cs`

#### Constructor Changes
- Added `IScopeResolver scopeResolver` parameter to constructor
- Updated dependency injection chain

#### Authorization Code Flow
Added `tenant_id` claim logic after scope claim in JWT access token generation:
```csharp
// Add tenant_id claim if any custom (non-standard) scopes are granted
var hasCustomScopes = scopes.Any(s => !scopeResolver.IsStandardScope(s));
if (hasCustomScopes && user?.TenantId != Guid.Empty)
{
    accessClaims.Add(new("tenant_id", user!.TenantId.ToString()));
}
```

#### Refresh Token Flow
Added user lookup and `tenant_id` claim:
```csharp
// Add tenant_id claim if any custom (non-standard) scopes are granted
var hasCustomScopes = scopes.Any(s => !scopeResolver.IsStandardScope(s));
if (hasCustomScopes)
{
    var user = await db.Users.AsNoTracking()
        .FirstOrDefaultAsync(u => u.Id == tokenEntity.UserId, ct)
        .ConfigureAwait(false);
    if (user?.TenantId != Guid.Empty)
    {
        accessClaims.Add(new("tenant_id", user!.TenantId.ToString()));
    }
}
```

#### Client Credentials (M2M) Flow
Added `tenant_id` claim for machine-to-machine tokens:
```csharp
if (granted.Count > 0)
{
    claims.Add(new("scope", string.Join(' ', granted)));
    
    // Add tenant_id claim if any custom (non-standard) scopes are granted
    var hasCustomScopes = granted.Any(s => !scopeResolver.IsStandardScope(s));
    if (hasCustomScopes && client.TenantId != Guid.Empty)
    {
        claims.Add(new("tenant_id", client.TenantId.ToString()));
    }
}
```

#### Token Exchange Flow
Added tenant context propagation:
```csharp
// Capture tenant_id claim if present in subject token
subjectTenantId = principal.FindFirst("tenant_id")?.Value;

// Later when issuing new token:
var hasCustomScopes = resultScopes.Any(s => !scopeResolver.IsStandardScope(s));
if (hasCustomScopes && !string.IsNullOrEmpty(subjectTenantId))
{
    claims.Add(new System.Security.Claims.Claim("tenant_id", subjectTenantId));
}
```

### 2. Test Infrastructure

**File:** `MrWhoOidc.UnitTests/Helpers/MockScopeResolver.cs` (new)

Created mock implementation for testing:
- Returns standard OAuth2/OIDC scopes as global scopes
- Returns test custom scopes (`custom.read`, `custom.write`) for tenant-scoped tests
- Implements scope validation and name availability checks
- Provides `IsStandardScope()` method matching production implementation

### 3. Unit Test Updates

Updated all existing token service tests to include `MockScopeResolver`:

**Files Modified:**
- `TokenExchangePolicyTests.cs` (6 test methods updated)
- `TokenExchangeTests.cs` (3 test methods updated)

**Pattern Applied:**
```csharp
var scopeResolver = new MockScopeResolver();
var svc = new TokenService(db, jwt, refresh, opts, meta, validator, 
                           settingsService, scopeResolver, oboPolicy);
```

## Behavior

### When tenant_id Claim is Added

The `tenant_id` claim is added to JWT access tokens when:

1. **At least one custom scope is granted** (determined by `!scopeResolver.IsStandardScope(scope)`)
2. **User/client has a valid tenant** (`TenantId != Guid.Empty`)

### Standard Scopes (No tenant_id Added)

The following scopes are considered standard and do NOT trigger `tenant_id` claim addition:
- `openid`
- `profile`
- `email`
- `address`
- `phone`
- `offline_access`
- `roles`

### Custom Scopes (tenant_id Added)

Any scope not in the standard list is considered custom and triggers `tenant_id` claim when present:
- Tenant-created scopes (e.g., `inventory.read`, `orders.write`)
- Custom business scopes

## Example Token Claims

### Token with Only Standard Scopes
```json
{
  "sub": "user-id",
  "scope": "openid profile email",
  "jti": "...",
  "iss": "https://auth.example.com",
  "aud": "api"
}
```

### Token with Custom Scopes
```json
{
  "sub": "user-id",
  "scope": "openid profile custom.read custom.write",
  "tenant_id": "tenant-guid",
  "jti": "...",
  "iss": "https://auth.example.com",
  "aud": "api"
}
```

## Security Implications

1. **Tenant Isolation:** Downstream APIs can validate that custom scopes match the tenant context
2. **Scope Validation:** APIs receiving tokens can verify custom scopes belong to the claimed tenant
3. **Multi-Tenant APIs:** Services can use `tenant_id` + custom scopes to enforce data access boundaries

## Testing

All existing unit tests pass with the new `MockScopeResolver`:
- Token Exchange Policy Tests: 6 tests ✅
- Token Exchange Tests: 3 tests ✅
- Build: Clean with 9 pre-existing warnings (unrelated to changes)

## Next Steps (Phase 3)

1. **Scope Naming Validation**
   - Enforce tenant-prefix conventions for custom scopes
   - Prevent name collisions across tenants

2. **Client Edit UI Updates**
   - Show available scopes (global + tenant) with visual grouping
   - Distinguish standard vs custom scopes in assignment UI

3. **Comprehensive Scope Tests**
   - Tenant isolation tests
   - Scope resolution edge cases
   - Validation boundary tests

## Files Changed

1. `MrWhoOidc.Auth/Services/TokenService.cs` - Added scope resolver integration
2. `MrWhoOidc.UnitTests/Helpers/MockScopeResolver.cs` - New mock for testing
3. `MrWhoOidc.UnitTests/TokenExchangePolicyTests.cs` - Updated 6 test methods
4. `MrWhoOidc.UnitTests/TokenExchangeTests.cs` - Updated 3 test methods

## Build Status

✅ **Success** - Clean build with zero errors
- All projects compile successfully
- All modified tests pass
- 9 pre-existing warnings (unrelated to scope changes)
