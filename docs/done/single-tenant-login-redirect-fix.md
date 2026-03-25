# Single-Tenant Login Redirect Fix

> Historical note: This fix summary is kept for implementation history. Route examples and tenant behavior notes should be read as point-in-time context; use [README](../../README.md), [docs/developer-guide.md](../developer-guide.md), and [docs/production-setup-guide.md](../production-setup-guide.md) for current behavior and setup.

**Issue:** When logging in to a single-tenant instance (MultiTenancy.Enabled = false), the Login page redirects to `/t/default/` which returns 404 Not Found because routes are registered at root level in single-tenant mode.

**Date Fixed:** October 6, 2025  
**Status:** ✅ RESOLVED

---

## Problem Analysis

### Root Cause
The `Login.cshtml.cs` page model was building tenant-aware redirect URLs regardless of whether multi-tenant mode was enabled:

```csharp
// OLD CODE (BROKEN):
var currentTenant = tenantAccessor.CurrentTenant;
var defaultUrl = currentTenant != null 
    ? $"/t/{currentTenant.Slug}/"  // ❌ Always builds /t/{slug}/ even in single-tenant mode
    : "/";
```

### Why This Caused a 404

In single-tenant mode:
1. **Route Registration:** Routes are registered at root level (`/Admin/Users`, `/Account/Sessions`, etc.) - NOT under `/t/{slug}/`
2. **Tenant Resolution:** `TenantResolutionMiddleware` resolves the default tenant without requiring `/t/{slug}` prefix
3. **Login Redirect:** Login page was still redirecting to `/t/default/` which doesn't exist

In multi-tenant mode:
1. **Route Registration:** Routes are registered under `/t/{slug}/` (`/t/acme/Admin/Users`, etc.)
2. **Tenant Resolution:** Middleware parses path for `/t/{slug}` and resolves tenant
3. **Login Redirect:** Correctly redirects to `/t/{slug}/`

---

## Solution Implemented

### Option 1: Mode-Aware Redirect (CHOSEN ✅)

Updated `Login.cshtml.cs` to check `IMultiTenancyOptions.Enabled` before building tenant-specific URLs.

**File Changed:** `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs`

**Changes:**

1. **Added dependency injection for `IMultiTenancyOptions`:**
   ```csharp
   public class LoginModel(
       IUserService users, 
       ILogger<LoginModel> logger,
       ITenantAccessor tenantAccessor,
       IMultiTenancyOptions multiTenancyOptions) : PageModel  // NEW
   ```

2. **Updated redirect logic to respect mode:**
   ```csharp
   // Build tenant-aware default redirect URL based on mode
   var currentTenant = tenantAccessor.CurrentTenant;
   string defaultUrl;
   
   if (multiTenancyOptions.Enabled && currentTenant != null)
   {
       // Multi-tenant mode: redirect to /t/{slug}/
       defaultUrl = $"/t/{currentTenant.Slug}/";
       logger.LogInformation("➡️ [Login] Multi-tenant mode: redirecting to {DefaultUrl} (Tenant: {TenantSlug})", 
           defaultUrl, currentTenant.Slug);
   }
   else
   {
       // Single-tenant mode: redirect to root /
       defaultUrl = "/";
       logger.LogInformation("➡️ [Login] Single-tenant mode: redirecting to {DefaultUrl}", defaultUrl);
   }
   
   return LocalRedirect(defaultUrl);
   ```

**Benefits:**
- ✅ Clean separation of concerns
- ✅ Login page respects multi-tenancy mode
- ✅ No route conflicts
- ✅ Minimal code change (single file)
- ✅ Better logging for troubleshooting

---

## Alternative Approach Considered (NOT CHOSEN)

### Option 2: Allow /t/default/ in Single-Tenant Mode

Modify `TenantResolutionMiddleware` to accept `/t/default/` even when multi-tenant is disabled.

**Why Rejected:**
- ❌ Routes still registered at root level, so nested paths like `/t/default/Admin/Users` would still 404
- ❌ Misleading (implies multi-tenant when it's not)
- ❌ More complex middleware logic
- ❌ Doesn't solve the underlying architectural mismatch

---

## Testing

### Manual Testing Steps

#### Test 1: Single-Tenant Mode Login
1. Ensure `appsettings.json` has `MultiTenancy.Enabled = false`
2. Start application: `dotnet run --project MrWhoOidc.AppHost`
3. Navigate to: `https://localhost:7208/login?email=admin%40mrwho.local`
4. Enter password and submit
5. **Expected:** Redirects to `https://localhost:7208/` (root)
6. **Before Fix:** Redirected to `https://localhost:7208/t/default/` (404)

#### Test 2: Multi-Tenant Mode Login
1. Ensure `appsettings.json` has `MultiTenancy.Enabled = true`
2. Start application
3. Navigate to: `https://localhost:7208/t/acme/login?email=user%40acme.com`
4. Enter password and submit
5. **Expected:** Redirects to `https://localhost:7208/t/acme/`
6. **Should still work** (no regression)

#### Test 3: Login with ReturnUrl
1. Navigate to: `https://localhost:7208/login?ReturnUrl=%2FAccount%2FProfile`
2. Enter credentials and submit
3. **Expected:** Redirects to `https://localhost:7208/Account/Profile` (ReturnUrl takes precedence)
4. **Should work in both modes**

### Automated Testing

All existing tests pass:
```bash
dotnet test
# Result: 331 tests passed ✅
```

No new tests added because:
- This is a UI-level redirect behavior
- Existing `TenantResolutionTests` already validate middleware behavior
- Existing `AdminUiMultiTenantRoutingTests` validate route registration per mode

---

## Configuration Reference

### Single-Tenant Mode (Default)
**File:** `MrWhoOidc.WebAuth/appsettings.json`

```json
{
  "MultiTenancy": {
    "Enabled": false,
    "DefaultTenantSlug": "default"
  }
}
```

**Behavior:**
- Routes registered at root level: `/`, `/Admin/Users`, `/Account/Sessions`
- Login redirects to: `/` (root)
- Tenant resolution: Always returns "default" tenant
- No `/t/{slug}` prefix required or accepted (will 404)

### Multi-Tenant Mode
**File:** `MrWhoOidc.WebAuth/appsettings.json`

```json
{
  "MultiTenancy": {
    "Enabled": true,
    "DefaultTenantSlug": "default"
  }
}
```

**Behavior:**
- Routes registered under `/t/{slug}/`: `/t/acme/`, `/t/acme/Admin/Users`, `/t/acme/Account/Sessions`
- Login redirects to: `/t/{slug}/` (tenant-specific)
- Tenant resolution: Parses path for `/t/{slug}` and looks up tenant in database
- `/t/{slug}` prefix required for all admin/account pages

---

## Related Components

### 1. TenantResolutionMiddleware
**File:** `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs`

**Responsibilities:**
- Resolve current tenant early in request pipeline
- Set `TenantContext` via `ITenantAccessor`
- Return 404 if tenant not found (multi-tenant mode)
- Return 500 if default tenant not found (config error)

**Behavior:**
- Single-tenant mode: Always resolves to default tenant
- Multi-tenant mode: Parses `/t/{slug}` from path

### 2. ModeAwareTenantResolver
**File:** `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`

**Responsibilities:**
- Extract tenant slug from path (multi-tenant mode)
- Look up tenant in database
- Cache tenant lookups (5 minutes)

**Methods:**
- `ResolveTenantAsync`: Main resolution logic
- `ResolveDefaultTenantAsync`: Returns default tenant (single-tenant fallback)
- `ResolveTenantBySlugAsync`: Looks up specific tenant by slug

### 3. EndpointMappingExtensions
**File:** `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`

**Responsibilities:**
- Register routes based on multi-tenancy mode
- Single-tenant: Root-level routes (`/Admin/Users`)
- Multi-tenant: Tenant-prefixed routes (`/t/{tenantId}/Admin/Users`)

---

## Logging

### Before Fix
```
[Login Page GET] ReturnUrl: (null), Email: admin@mrwho.local
✅ [Login] User admin signed in successfully
➡️ [Login] No valid ReturnUrl, redirecting to /t/default/ (Tenant: default)
```

### After Fix (Single-Tenant)
```
[Login Page GET] ReturnUrl: (null), Email: admin@mrwho.local
✅ [Login] User admin signed in successfully
➡️ [Login] Single-tenant mode: redirecting to /
```

### After Fix (Multi-Tenant)
```
[Login Page GET] ReturnUrl: (null), Email: user@acme.com
✅ [Login] User user@acme.com signed in successfully
➡️ [Login] Multi-tenant mode: redirecting to /t/acme/ (Tenant: acme)
```

---

## Deployment Notes

### Production Checklist
- [x] Code change deployed to production
- [ ] Verify `appsettings.json` has correct `MultiTenancy.Enabled` setting
- [ ] Test login flow in production environment
- [ ] Monitor logs for redirect behavior

### Rollback Plan
If issues arise:
1. Revert `Login.cshtml.cs` to previous version
2. Restart application
3. Login will redirect to `/t/default/` again (404 in single-tenant mode)

### Migration Path
No database migrations required - this is purely a UI redirect fix.

---

## Future Considerations

### 1. Dynamic Mode Switching
Currently, multi-tenancy mode is set via `appsettings.json` and requires app restart to change.

**Future Enhancement:**
- Support runtime mode switching
- Store mode in database (`TenantSettings` table)
- Update all mode-dependent components to query database

### 2. Tenant-Specific Login Pages
In multi-tenant mode, each tenant could have custom branding on login page.

**Future Enhancement:**
- Create `/t/{slug}/login` variant
- Load tenant-specific CSS/logos
- Maintain fallback to root `/login` for default tenant

### 3. ReturnUrl Validation
Currently, `ReturnUrl` must be local URL (`Url.IsLocalUrl()`).

**Future Enhancement:**
- Validate `ReturnUrl` starts with correct prefix (`/` for single-tenant, `/t/{slug}/` for multi-tenant)
- Prevent cross-tenant redirects in multi-tenant mode

---

## Summary

**Problem:** Single-tenant login redirected to `/t/default/` (404)  
**Root Cause:** Login page always built tenant-prefixed URLs  
**Solution:** Check `IMultiTenancyOptions.Enabled` before building URLs  
**Impact:** Single-tenant login now redirects to `/` (root) correctly  
**Regression Risk:** Low (multi-tenant mode unaffected, all tests pass)

**Files Changed:** 1 (Login.cshtml.cs)  
**Lines Changed:** ~20  
**Build Status:** ✅ Success (with unrelated file lock warnings)  
**Test Status:** ✅ All 331 tests passing
