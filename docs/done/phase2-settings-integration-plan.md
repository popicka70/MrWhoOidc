# Phase 2.5/3: Settings Integration Implementation Plan

**Date:** October 8, 2025  
**Goal:** Wire tenant settings into actual OIDC/Auth flows  
**Status:** In Progress

## Integration Points

### 1. Token Lifetimes ⏱️ **PRIORITY 1**

**Current:** Hardcoded in TokenService
- Access tokens: 15 minutes (900 seconds)
- ID tokens: 5 minutes (300 seconds)  
- Refresh tokens: Managed by RefreshTokenService
- Auth codes: Managed by AuthorizationCodeStore

**Target Settings:**
- `Tokens.AccessTokenLifetimeSeconds` (default: 3600)
- `Tokens.IdTokenLifetimeSeconds` (default: 3600)
- `Tokens.RefreshTokenLifetimeSeconds` (default: 1296000 = 15 days)
- `Tokens.AuthorizationCodeLifetimeSeconds` (default: 300)

**Files to Modify:**
- ✅ `MrWhoOidc.Auth/Services/TokenService.cs` - Use settings for access/ID token expiry
- ⏳ `MrWhoOidc.Auth/Services/RefreshTokenService.cs` - Use settings for refresh token expiry
- ⏳ `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs` - Use settings for code expiry

### 2. Password Policy 🔑 **PRIORITY 2**

**Current:** Likely hardcoded or using Identity defaults

**Target Settings:**
- `Auth.PasswordPolicy.MinLength`
- `Auth.PasswordPolicy.RequireUppercase`
- `Auth.PasswordPolicy.RequireLowercase`
- `Auth.PasswordPolicy.RequireDigit`
- `Auth.PasswordPolicy.RequireSpecialChar`

**Files to Modify:**
- ⏳ `MrWhoOidc.Auth/Services/UserService.cs` - Apply policy on user creation/password change
- ⏳ User registration page handler
- ⏳ Password change page handler

### 3. QR Login Settings 📱 **PRIORITY 3**

**Current:** Configuration-based via `QrLoginOptions`

**Target Settings:**
- `QrLogin.Enabled`
- `QrLogin.SessionLifetimeSeconds`

**Files to Modify:**
- ⏳ `MrWhoOidc.WebAuth/Background/QrLoginCleanupService.cs` - Check enabled flag
- ⏳ QR login pages - Check enabled, use lifetime
- ⏳ QR session creation - Use tenant lifetime

### 4. MFA Enforcement 🔐 **PRIORITY 4**

**Current:** Optional per user

**Target Settings:**
- `Auth.RequireMfa` - Require for all users in tenant

**Files to Modify:**
- ⏳ Login flow - Check if MFA required for tenant
- ⏳ User authentication handler

### 5. Introspection 🔍 **PRIORITY 5**

**Current:** Configuration-based

**Target Settings:**
- `Auth.AllowRefreshTokenIntrospection`

**Files to Modify:**
- ⏳ Introspection endpoint handler

### 6. PKCE 🔒 **FUTURE**

**Target Settings:**
- `Oidc.RequirePkce`

**Files to Modify:**
- ⏳ Authorization endpoint - Enforce PKCE if required

---

## Implementation Strategy

### Phase 2.5 (Today): Token Lifetimes
1. ✅ Inject `ITenantSettingsService` into `TokenService`
2. ✅ Replace hardcoded lifetimes with settings
3. ✅ Add fallback defaults
4. ✅ Test token generation

### Phase 3 (Future):
1. Password policy integration
2. QR login integration
3. MFA enforcement
4. Introspection settings
5. PKCE enforcement

---

## Testing Plan

### Unit Tests
- [ ] Token lifetime respects tenant settings
- [ ] Fallback to platform defaults works
- [ ] Null tenant context uses platform defaults

### Integration Tests
- [ ] E2E token flow with custom lifetimes
- [ ] Password validation with custom policy
- [ ] QR login disabled per tenant

---

## Progress Tracking

| Component | Status | Files Modified |
|-----------|--------|----------------|
| Token Lifetimes | 🔄 In Progress | TokenService.cs |
| Password Policy | 📋 Planned | - |
| QR Login | 📋 Planned | - |
| MFA Enforcement | 📋 Planned | - |
| Introspection | 📋 Planned | - |
| PKCE | 📋 Future | - |

---

**Started:** October 8, 2025  
**Current Focus:** Token lifetime integration
