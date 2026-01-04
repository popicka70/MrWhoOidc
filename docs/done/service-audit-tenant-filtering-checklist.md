# Service Audit: Tenant Filtering Checklist

**Purpose:** Verify all core services properly filter by TenantId to ensure multi-tenant data isolation  
**Date:** October 14, 2025  
**Status:** 🔍 Audit in Progress

---

## Audit Scope

### Services to Audit (8 Core Services)

1. ✅ **ConsentService** - `MrWhoOidc.Auth/Services/ConsentService.cs`
2. ✅ **RefreshTokenService** - `MrWhoOidc.Auth/Services/RefreshTokenService.cs`
3. ✅ **AuthorizationCodeService** - `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs`
4. 🔍 **UserService** - `MrWhoOidc.Auth/Services/UserService.cs`
5. ✅ **ClientStore** - `MrWhoOidc.Auth/Stores/ClientStore.cs`
6. ✅ **KeyStore** - `MrWhoOidc.Auth/Stores/KeyStore.cs`
7. 🔍 **TokenValidator** - `MrWhoOidc.Auth/Services/TokenValidator.cs`
8. 🔍 **JwtService** - `MrWhoOidc.Auth/Services/JwtService.cs`

---

## 1. ConsentService

**Status:** ✅ Already Verified (via DataIsolationTests.cs)

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `GrantConsentAsync()` | ✅ Sets TenantId | ✅ DataIsolationTests | Creates consent with current TenantId |
| `HasConsentAsync()` | ✅ Filters by TenantId | ✅ DataIsolationTests | Queries only current tenant's consents |
| `RevokeConsentAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should filter by TenantId when revoking |
| `GetConsentsAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should return only current tenant's consents |

### Verification Priority: LOW (core methods already verified)

---

## 2. RefreshTokenService

**Status:** ✅ Already Verified (via DataIsolationTests.cs)

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `CreateRefreshTokenAsync()` | ✅ Sets TenantId | ✅ DataIsolationTests | Creates token with current TenantId |
| `ValidateRefreshTokenAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate against current tenant only |
| `RevokeRefreshTokenAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should revoke only in current tenant |
| `CleanupExpiredTokensAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should clean only current tenant's tokens |

### Verification Priority: MEDIUM (create verified, validate/revoke need checks)

---

## 3. AuthorizationCodeService

**Status:** ✅ Already Verified (via DataIsolationTests.cs)

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `IssueAsync()` | ✅ Sets TenantId | ✅ DataIsolationTests | Creates code with current TenantId |
| `ValidateAndConsumeAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate against current tenant only |
| `CleanupExpiredCodesAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should clean only current tenant's codes |

### Verification Priority: MEDIUM (issue verified, validate/cleanup need checks)

---

## 4. UserService

**Status:** 🔍 **Needs Full Audit**

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `GetUserAsync()` | 🔍 **Needs Verification** | ✅ Partial (DataIsolationTests) | Should return only current tenant's users |
| `ValidateCredentialsAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate against current tenant only |
| `CreateUserAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should set TenantId correctly |
| `UpdateUserAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should update only current tenant's users |
| `FindByUsernameAsync()` | 🔍 **Needs Verification** | ❌ Not tested | Should filter by TenantId (username not unique globally) |

### Verification Priority: HIGH (critical authentication path)

---

## 5. ClientStore

**Status:** ✅ Already Well-Covered (existing multi-tenant tests)

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `GetClientAsync()` | ✅ Filters by TenantId | ✅ MultiTenantE2ETests | Returns client from current tenant only |
| `GetClientsAsync()` | ✅ Filters by TenantId | ✅ Admin UI tests | Lists clients for current tenant only |

### Verification Priority: LOW (already well-tested)

---

## 6. KeyStore

**Status:** ✅ Already Well-Covered (JWKS tests)

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `GetActiveSigningKeyAsync()` | ✅ Filters by TenantId | ✅ JwksMultiTenancyTests | Returns key for current tenant only |
| `GetPublicJwksAsync()` | ✅ Filters by TenantId | ✅ JwksMultiTenancyTests | Returns JWKS for current tenant only |
| `RotateKeyAsync()` | ✅ Scoped to TenantId | ✅ JwksMultiTenancyTests | Rotates keys per tenant independently |

### Verification Priority: LOW (already well-tested)

---

## 7. TokenValidator

**Status:** 🔍 **Needs Audit**

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `ValidateAccessToken()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate issuer matches current tenant |
| `ValidateIdToken()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate issuer matches current tenant |
| `ValidateRefreshToken()` | 🔍 **Needs Verification** | ❌ Not tested | Should validate token belongs to current tenant |

### Verification Priority: HIGH (critical security path)

---

## 8. JwtService

**Status:** 🔍 **Needs Audit**

### Methods Requiring TenantId Filtering

| Method | TenantId Filter? | Test Coverage | Notes |
|--------|------------------|---------------|-------|
| `CreateJwt()` | 🔍 **Needs Verification** | ✅ Partial (E2E tests) | Should use current tenant's issuer and key |
| `SignJwt()` | 🔍 **Needs Verification** | ✅ Partial (E2E tests) | Should use current tenant's signing key |

### Verification Priority: MEDIUM (already partially covered by E2E tests)

---

## Test Implementation Plan

### High Priority (Service Audit Tests)

**File:** `MrWhoOidc.UnitTests/MultiTenancy/ServiceAuditTests.cs`

1. **UserService_GetUserAsync_FiltersByTenantId**
   - Create user in Tenant A
   - Switch to Tenant B context
   - Verify GetUserAsync returns null for Tenant A's user

2. **UserService_ValidateCredentials_FiltersByTenantId**
   - Create user "alice" with password in Tenant A
   - Create user "alice" with different password in Tenant B
   - Verify credential validation respects tenant context

3. **RefreshTokenService_ValidateRefreshToken_RejectsOtherTenant**
   - Create refresh token in Tenant A
   - Switch to Tenant B context
   - Verify token validation fails

4. **AuthorizationCodeService_ValidateAndConsume_RejectsOtherTenant**
   - Issue auth code in Tenant A
   - Switch to Tenant B context
   - Verify code validation fails

### Expected Test Count

- **Service Audit Tests:** 3-5 new tests
- **Total after Service Audit:** 66-68 tests

---

## Findings & Recommendations

### ✅ Well-Isolated Services
- **ClientStore** - Comprehensive tenant filtering
- **KeyStore** - Full tenant isolation verified
- **ConsentService** - Core methods verified
- **RefreshTokenService** - Create method verified
- **AuthorizationCodeService** - Issue method verified

### 🔍 Needs Verification
- **UserService** - Authentication methods need explicit testing
- **TokenValidator** - Issuer validation needs tenant-aware testing
- **RefreshTokenService** - Validate/Revoke methods need testing
- **AuthorizationCodeService** - Validate/Consume needs testing

### 📋 Recommendations

1. **Service Audit Tests** - Add 3-5 focused tests for high-priority gaps
2. **Issuer Validation** - Verify TokenValidator checks issuer against tenant
3. **Cross-Tenant Security** - Add tests for token/code redemption across tenants
4. **Username Uniqueness** - Verify username is unique per tenant (not globally)

---

**Status Legend:**
- ✅ Verified - Test coverage exists and passing
- 🔍 Needs Verification - Requires explicit test coverage
- ❌ Not Tested - No test coverage found

**Next Steps:**
1. Implement ServiceAuditTests.cs with 3-5 high-priority tests
2. Run tests and verify 100% pass rate
3. Update this checklist with findings
4. Document any discovered issues

---

**Last Updated:** October 14, 2025  
**Audited By:** AI Assistant  
**Review Status:** Ready for Implementation
