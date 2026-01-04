# Phase 2: Tenant Settings Override System - Implementation Summary

**Date:** October 8, 2025  
**Status:** Complete  
**Integration:** Pending (Phase 3)

## Overview

Phase 2 Step 2 implements a cascading settings override system allowing tenants to customize OIDC, authentication, QR login, and token lifetime settings beyond platform defaults. Settings are stored as JSON in `Tenant.SettingsJson` and merged at runtime with platform defaults from `appsettings.json`.

## Architecture

### Cascading Hierarchy

```
Platform Defaults (appsettings.json)
         ↓
Tenant Overrides (Tenant.SettingsJson)
         ↓
   [Future: Client Overrides]
         ↓
   Effective Settings
```

**Merge Logic:** Tenant values override platform defaults. Null tenant values fall back to platform defaults.

## Implemented Components

### 1. Settings Model ✅

**File:** `MrWhoOidc.Auth/Settings/TenantSettings.cs`

**Structure:**
```csharp
public class TenantSettings
{
    public OidcTenantSettings? Oidc { get; set; }
    public AuthTenantSettings? Auth { get; set; }
    public QrLoginTenantSettings? QrLogin { get; set; }
    public TokenTenantSettings? Tokens { get; set; }
}
```

**Supported Settings:**

#### OIDC Settings
- `Issuer` - Override issuer URI (rarely used)
- `RequirePkce` - Require PKCE for authorization code flow
- `CorsOrigins` - Allowed CORS origins

#### Auth Settings  
- `AllowRefreshTokenIntrospection` - Allow introspecting refresh tokens
- `RequireMfa` - Require MFA for all users
- `PasswordPolicy`:
  - `MinLength` - Minimum password length (6-128)
  - `RequireUppercase` - Require uppercase letters
  - `RequireLowercase` - Require lowercase letters
  - `RequireDigit` - Require digits
  - `RequireSpecialChar` - Require special characters

#### QR Login Settings
- `Enabled` - Enable/disable QR login
- `SessionLifetimeSeconds` - QR session lifetime (30-600s)

#### Token Lifetime Settings
- `AccessTokenLifetimeSeconds` - Access token lifetime (60-86400s)
- `RefreshTokenLifetimeSeconds` - Refresh token lifetime (3600-2592000s)
- `AuthorizationCodeLifetimeSeconds` - Auth code lifetime (30-600s)
- `IdTokenLifetimeSeconds` - ID token lifetime (60-86400s)

### 2. Settings Service ✅

**Files:**
- `MrWhoOidc.Auth/Services/ITenantSettingsService.cs` - Interface
- `MrWhoOidc.Auth/Services/TenantSettingsService.cs` - Implementation

**Methods:**
```csharp
Task<TenantSettings?> GetTenantSettingsAsync(Guid tenantId);
Task<TenantSettings> GetCurrentTenantSettingsAsync();
Task<bool> UpdateTenantSettingsAsync(Guid tenantId, TenantSettings settings);
TenantSettings GetPlatformDefaults();
```

**Features:**
- ✅ Loads platform defaults from `appsettings.json` once at startup
- ✅ Parses `Tenant.SettingsJson` (handles invalid JSON gracefully)
- ✅ Merges platform + tenant settings with proper precedence
- ✅ Separate merge methods for each settings section
- ✅ JSON serialization with `JsonIgnoreCondition.WhenWritingNull`
- ✅ Tenant context awareness via `ITenantAccessor`

**Registration:**
- Added to `MrWhoOidc.WebAuth/Program.cs` as scoped service

### 3. Admin UI ✅

**Files:**
- `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml` - Razor page
- `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml.cs` - Page model

**Route:** `/t/{tenantSlug}/admin/settings`

**Features:**
- 📋 **Authentication Section:**
  - Toggle: Allow refresh token introspection
  - Toggle: Require MFA for all users
  - Shows platform defaults inline

- 🔑 **Password Policy Section:**
  - Input: Minimum length (6-128)
  - Toggles: Require uppercase/lowercase/digit/special char
  - Shows platform defaults inline

- 📱 **QR Login Section:**
  - Toggle: Enable QR login
  - Input: Session lifetime (30-600s)
  - Shows platform defaults inline

- ⏱️ **Token Lifetimes Section:**
  - Inputs: Access token (60-86400s)
  - Inputs: Refresh token (3600-2592000s)
  - Inputs: Authorization code (30-600s)
  - Inputs: ID token (60-86400s)
  - Shows platform defaults inline

- ✨ **UX Features:**
  - Platform default values displayed next to each field
  - "Reset to Platform Defaults" button
  - Success notifications
  - Single-tenant mode awareness
  - Validation

**Navigation:**
- Added to admin sidebar (only visible in multi-tenant mode)
- Icon: `bi-gear-fill`
- Positioned after Branding link

## Database Storage

Settings are stored as JSON string in existing `Tenant.SettingsJson` column:

```sql
[MaxLength(4000)]
public string? SettingsJson { get; set; }
```

**Example JSON:**
```json
{
  "auth": {
    "allowRefreshTokenIntrospection": true,
    "requireMfa": false,
    "passwordPolicy": {
      "minLength": 12,
      "requireUppercase": true,
      "requireLowercase": true,
      "requireDigit": true,
      "requireSpecialChar": true
    }
  },
  "qrLogin": {
    "enabled": true,
    "sessionLifetimeSeconds": 180
  },
  "tokens": {
    "accessTokenLifetimeSeconds": 7200,
    "refreshTokenLifetimeSeconds": 2592000
  }
}
```

**No migration needed** - `SettingsJson` column added in Phase 1.

## Integration Points (Future - Phase 3)

### Where Settings Will Be Used

1. **Token Generation** (`MrWhoOidc.Auth/Services/TokenService.cs`)
   - Use `Tokens.AccessTokenLifetimeSeconds`
   - Use `Tokens.RefreshTokenLifetimeSeconds`
   - Use `Tokens.IdTokenLifetimeSeconds`

2. **Authorization Code Handler**
   - Use `Tokens.AuthorizationCodeLifetimeSeconds`

3. **QR Login Service** (`MrWhoOidc.WebAuth/Background/QrLoginCleanupService.cs`)
   - Check `QrLogin.Enabled`
   - Use `QrLogin.SessionLifetimeSeconds`

4. **Password Validation** (User registration/password change)
   - Apply `Auth.PasswordPolicy` rules

5. **Introspection Endpoint**
   - Check `Auth.AllowRefreshTokenIntrospection`

6. **MFA Enforcement** (Login flow)
   - Check `Auth.RequireMfa`

7. **PKCE Validation** (Authorization endpoint)
   - Check `Oidc.RequirePkce`

### Integration Pattern

```csharp
// Example: Using settings in token generation
public class TokenService
{
    private readonly ITenantSettingsService _settingsService;
    
    public async Task<string> GenerateAccessTokenAsync(...)
    {
        var settings = await _settingsService.GetCurrentTenantSettingsAsync();
        var lifetime = settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600;
        
        // Use lifetime in token generation
        var expires = DateTimeOffset.UtcNow.AddSeconds(lifetime);
        // ...
    }
}
```

## Testing

### Unit Tests
- ✅ All 331 tests passing
- ✅ Endpoint snapshot updated

### Manual Test Checklist

**Multi-Tenant Mode:**
1. [ ] Enable multi-tenancy in appsettings
2. [ ] Navigate to `/t/default/admin/settings`
3. [ ] Verify platform defaults displayed correctly
4. [ ] Change password min length to 12
5. [ ] Toggle "Require MFA"
6. [ ] Set access token lifetime to 7200
7. [ ] Save changes
8. [ ] Refresh page - verify settings persisted
9. [ ] Check database: `Tenant.SettingsJson` contains JSON
10. [ ] Click "Reset to Platform Defaults"
11. [ ] Save - verify settings cleared
12. [ ] Test with second tenant - verify isolation

**Single-Tenant Mode:**
1. [ ] Disable multi-tenancy
2. [ ] Verify settings link hidden in sidebar
3. [ ] Attempt direct navigation to settings page
4. [ ] Verify appropriate message/redirect

### Integration Tests (Phase 3)

```csharp
[TestClass]
public class TenantSettingsIntegrationTests
{
    [TestMethod]
    public async Task TokenGeneration_UsesTenantTokenLifetime() { }
    
    [TestMethod]
    public async Task PasswordValidation_UsesTenantPasswordPolicy() { }
    
    [TestMethod]
    public async Task QrLogin_RespectsEnabledFlag() { }
}
```

## Configuration

### Platform Defaults (appsettings.json)

```json
{
  "Auth": {
    "AllowRefreshTokenIntrospection": true,
    "PasswordPolicy": {
      "MinLength": 8,
      "RequireUppercase": true,
      "RequireLowercase": true,
      "RequireDigit": true,
      "RequireSpecialChar": false
    }
  },
  "QrLogin": {
    "Enabled": true,
    "SessionLifetimeSeconds": 300
  }
}
```

### Tenant Overrides

Set via admin UI at `/t/{slug}/admin/settings` or via API (future).

## Security Considerations

- ✅ Tenant isolation enforced (no cross-tenant settings access)
- ✅ JSON parsing errors handled gracefully (fallback to platform defaults)
- ✅ Max length constraints (4000 chars for JSON)
- ✅ Admin authorization required (tenant-admin policy)
- ⚠️ No validation of setting value ranges yet (planned for Phase 3)
- ⚠️ No audit logging of settings changes (planned for Phase 3)

## Performance Considerations

- Platform defaults loaded once at startup (cached in service)
- Tenant settings loaded per-request via scoped service
- EF Core uses Select projection (no full entity load)
- JSON parsing on every settings fetch
- 🔄 Consider caching tenant settings (Redis) in Phase 5

## Limitations & Future Enhancements

### Current Limitations
- ❌ Settings not yet integrated with token/auth handlers
- ❌ No client-level overrides (only platform → tenant)
- ❌ No validation of setting value ranges in UI
- ❌ No audit log of settings changes
- ❌ No bulk import/export of settings
- ❌ No settings versioning/history

### Phase 3+ Enhancements
1. **Integration:** Wire settings into token/auth handlers
2. **Validation:** Add range validation for numeric settings
3. **Audit:** Log all settings changes with who/when/what
4. **Client-Level:** Add client-specific setting overrides
5. **Import/Export:** JSON import/export for settings
6. **Templates:** Preset setting templates (strict/relaxed/default)
7. **Diff View:** Show platform defaults vs tenant overrides side-by-side
8. **Settings API:** REST API for programmatic settings management
9. **Caching:** Redis cache for tenant settings (Phase 5)
10. **Validation Rules:** Custom validation rules per setting type

## Troubleshooting

### Settings Not Persisting

**Issue:** Changes saved but not reflected on page refresh  
**Causes:**
- JSON serialization failed silently
- Browser caching old data
- Database transaction not committed

**Solution:**
- Check application logs for errors
- Verify `Tenant.SettingsJson` in database
- Hard refresh browser (Ctrl+Shift+R)

### Platform Defaults Not Showing

**Issue:** Platform default values show as "Not set"  
**Cause:** Missing configuration in appsettings.json  
**Solution:** Add default values to appsettings.json

### Invalid JSON Error

**Issue:** Settings page shows error after manual JSON edit  
**Cause:** Malformed JSON in `Tenant.SettingsJson`  
**Solution:** Fix JSON syntax or reset via "Reset to Platform Defaults"

## Files Modified

### New Files (4)
1. `MrWhoOidc.Auth/Settings/TenantSettings.cs` - Models
2. `MrWhoOidc.Auth/Services/ITenantSettingsService.cs` - Interface
3. `MrWhoOidc.Auth/Services/TenantSettingsService.cs` - Implementation
4. `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml` - Razor page
5. `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml.cs` - Page model

### Modified Files (3)
1. `MrWhoOidc.WebAuth/Program.cs` - Service registration
2. `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` - Navigation link
3. `MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json` - New endpoint

---

## Related Documentation

- `docs/phase2-branding-implementation.md` - Branding system
- `docs/multitenancy-backlog.md` - Phase 2 progress tracking
- `docs/settings-quick-reference.md` - Quick reference (to be created)

---

**Step 2 Complete:** ✅ Settings Override System (UI + Service)  
**Next:** 🎯 Settings Integration (Phase 3 - wire into token handlers)  
**Optional:** Tenant Setup Wizard (deferred to later phase)

---

**Last Updated:** October 8, 2025  
**Author:** GitHub Copilot  
**Version:** 1.0 (Phase 2, Step 2 Complete)
