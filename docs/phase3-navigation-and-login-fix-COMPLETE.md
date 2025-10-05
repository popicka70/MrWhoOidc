# Phase 3: Navigation & Login Redirect Fix - COMPLETE SOLUTION

## Problems Discovered

### Problem 1: Login Redirects to Root Path (Losing Tenant Context)
**Location**: `Pages/Login.cshtml.cs` line 104

**Code**:
```csharp
return RedirectToPage("/Index");  // ❌ WRONG - goes to root /Index
```

**Issue**: After successful login via `/t/pop-app/login`, the page redirected to `/Index` (root) instead of `/t/pop-app/`, causing:
1. User loses tenant context
2. Middleware resolves to "default" tenant (fallback behavior)
3. Menu links show `/t/default/Admin/*`
4. Authorization fails (user has roles in "pop-app", not "default")

### Problem 2: Menu Links Not Tenant-Aware
**Location**: `Pages/Shared/_Layout.cshtml`

**Issue**: Menu links generated as `/Admin/Realms` instead of `/t/{slug}/Admin/Realms`, causing loss of tenant context on navigation.

### Problem 3: Authorization Requires Tenant Context
**Location**: `Security/Admin/TenantAdminAuthorizationHandler.cs` lines 68-72

**Code**:
```csharp
var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
if (tenantId == null)
{
    // No tenant context - cannot proceed
    return;  // ❌ Authorization fails
}
```

**Issue**: Authorization handler checks if user has `tenant-admin` role in **current tenant**. If tenant context is null or wrong tenant, authorization fails with "Access Denied".

## How Tenant Resolution Works

### The Middleware Flow (`TenantResolutionMiddleware` + `ModeAwareTenantResolver`)

1. **Request arrives**: e.g., `/t/pop-app/Admin/Users`
2. **Middleware extracts slug**: Parses path → finds `pop-app`
3. **Looks up tenant**: Queries database for tenant with slug `pop-app`
4. **Sets context**: `ITenantAccessor.CurrentTenant` = found tenant
5. **Request proceeds**: Controllers/pages can access `CurrentTenant`

### Important Behavior

**Path has `/t/{slug}/`**: Resolves that specific tenant
```
/t/pop-app/Admin/Users → CurrentTenant.Slug = "pop-app" ✅
/t/acme/Admin/Clients  → CurrentTenant.Slug = "acme" ✅
```

**Path has NO `/t/{slug}/`**: Falls back to default tenant
```
/Admin/Users           → CurrentTenant.Slug = "default" ⚠️
/Index                 → CurrentTenant.Slug = "default" ⚠️
```

## The Complete Solution

### Fix 1: Login Redirect Preserves Tenant Context

**File**: `Pages/Login.cshtml.cs`

**Changes**:
1. Inject `ITenantAccessor` in constructor
2. Build tenant-aware redirect URL
3. Redirect to `/t/{slug}/` instead of `/`

**Code**:
```csharp
public class LoginModel(
    IUserService users, 
    ILogger<LoginModel> logger,
    ITenantAccessor tenantAccessor) : PageModel  // ← Added injection
{
    // ... login logic ...
    
    // After successful authentication:
    if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
    {
        return LocalRedirect(ReturnUrl);
    }

    // Build tenant-aware default redirect URL
    var currentTenant = tenantAccessor.CurrentTenant;
    var defaultUrl = currentTenant != null 
        ? $"/t/{currentTenant.Slug}/"     // ← Tenant-aware!
        : "/";
    
    logger.LogInformation("➡️ [Login] No valid ReturnUrl, redirecting to {DefaultUrl} (Tenant: {TenantSlug})", 
        defaultUrl, 
        currentTenant?.Slug ?? "(none)");
    
    return LocalRedirect(defaultUrl);  // ← Preserves tenant context!
}
```

### Fix 2: Menu Links Preserve Tenant Context

**File**: `Pages/Shared/_Layout.cshtml`

**Changes**:
1. Inject `ITenantAccessor`
2. Build `tenantPrefix` from `CurrentTenant.Slug`
3. Use `href="@(tenantPrefix)/Admin/{Page}"` instead of `asp-page`

**Code**:
```cshtml
@inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor

@{
    var currentTenant = TenantAccessor.CurrentTenant;
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
}

<!-- Admin menu links -->
<a href="@(tenantPrefix)/Admin/Realms">Realms</a>
<a href="@(tenantPrefix)/Admin/Clients">Clients</a>
<a href="@(tenantPrefix)/Admin/Users">Users</a>
<!-- ... etc ... -->
```

## Complete User Flow (After Fix)

### Tenant Admin Login Flow

1. **User goes to**: `https://localhost:8443/DiscoverTenant`
2. **Enters email**: `admin@pop.app`
3. **System redirects to**: `https://localhost:8443/t/pop-app/login?email=admin%40pop.app`
4. **Middleware resolves**: Tenant = "pop-app"
5. **User logs in**: Credentials validated
6. **Login redirects to**: `https://localhost:8443/t/pop-app/` ✅ (NOT `/`)
7. **Home page loads**: Middleware resolves tenant from path
8. **Menu links show**: `/t/pop-app/Admin/Realms`, `/t/pop-app/Admin/Users`, etc. ✅
9. **User clicks "Users"**: Navigates to `https://localhost:8443/t/pop-app/Admin/Users`
10. **Middleware resolves**: Tenant = "pop-app" ✅
11. **Authorization checks**:
    - CurrentTenant = "pop-app"
    - User has `tenant-admin` role in pop-app tenant
    - Authorization succeeds ✅
12. **Page loads**: Shows only pop-app tenant's users ✅

### Platform Admin Login Flow

1. **User goes to**: `https://localhost:8443/login` (direct, no tenant)
2. **Middleware resolves**: Default tenant (or null)
3. **User logs in**: Credentials validated
4. **Login redirects to**: `https://localhost:8443/` (root)
5. **Menu links show**: `/Admin/Realms`, `/Admin/Users`, etc.
6. **User clicks "Users"**: Navigates to `https://localhost:8443/Admin/Users`
7. **Middleware resolves**: Default tenant
8. **Authorization checks**:
    - User has `platform-admin` role
    - Platform admins bypass tenant checks
    - Authorization succeeds ✅
9. **Page loads**: Shows all tenants with filter dropdowns ✅

## Testing Steps

### 1. Rebuild and Restart
```powershell
dotnet build
docker compose down
docker compose up -d --build
```

### 2. Test Tenant Admin Flow
1. Go to: `https://localhost:8443/DiscoverTenant`
2. Enter: `admin@pop.app`
3. **Verify redirect URL**: Should be `https://localhost:8443/t/pop-app/login?email=...`
4. Login with password
5. **Verify landing URL**: Should be `https://localhost:8443/t/pop-app/` ✅ (NOT just `/`)
6. **Check browser DevTools Network tab**: Confirm redirect to `/t/pop-app/`
7. **Inspect HTML menu links**: Should show `href="/t/pop-app/Admin/Realms"` ✅
8. Click "Users" menu item
9. **Verify URL**: Should be `https://localhost:8443/t/pop-app/Admin/Users` ✅
10. **Verify page loads**: No "Access Denied" error ✅
11. **Verify data**: See only pop-app tenant's users ✅

### 3. Check Logs
Look for this log message after login:
```
➡️ [Login] No valid ReturnUrl, redirecting to /t/pop-app/ (Tenant: pop-app)
```

Should NOT see:
```
➡️ [Login] No valid ReturnUrl, redirecting to /Index
```

### 4. Verify Authorization
If you still get "Access Denied" after these fixes:
1. Check user has `tenant-admin` role assignment in pop-app tenant
2. Check role is in the "default" realm of pop-app tenant
3. Check role assignment is `IsActive = true`
4. Check role is `IsActive = true`

SQL to verify:
```sql
SELECT 
    u.Username,
    u.Email,
    t.Name as TenantName,
    rl.Name as RealmName,
    r.Name as RoleName,
    ura.IsActive as AssignmentActive,
    r.IsActive as RoleActive
FROM UserRoleAssignments ura
JOIN Users u ON ura.UserId = u.Id
JOIN Roles r ON ura.RoleId = r.Id
JOIN Realms rl ON r.RealmId = rl.Id
JOIN Tenants t ON rl.TenantId = t.Id
WHERE u.Email = 'admin@pop.app'
  AND r.Name = 'tenant-admin'
  AND rl.Name = 'default'
  AND t.Slug = 'pop-app';
```

Expected result: 1 row with AssignmentActive = true, RoleActive = true

## Why This Fix Works

### Path-Based Tenant Context is Preserved

**Before Fix**:
```
Login at: /t/pop-app/login
Redirect to: /Index                    ❌ Lost tenant context
Menu links: /Admin/Users               ❌ Lost tenant context
Authorization: tenantId = null         ❌ FAILED
```

**After Fix**:
```
Login at: /t/pop-app/login
Redirect to: /t/pop-app/               ✅ Tenant context preserved
Menu links: /t/pop-app/Admin/Users     ✅ Tenant context preserved
Authorization: tenantId = pop-app      ✅ SUCCESS
```

### Every Request Maintains `/t/{slug}/` Prefix

Once a user logs in via a tenant-specific URL, **all subsequent navigation stays within that tenant's path space**, ensuring:
1. Middleware always resolves correct tenant
2. Authorization always checks correct tenant
3. Data queries always scope to correct tenant
4. User never accidentally accesses wrong tenant's data

## Files Modified

1. **`MrWhoOidc.WebAuth/Pages/Login.cshtml.cs`**
   - Added `ITenantAccessor` injection
   - Changed default redirect from `/Index` to `/t/{slug}/` or `/`

2. **`MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml`**
   - Added `ITenantAccessor` injection
   - Build `tenantPrefix` from `CurrentTenant.Slug`
   - All admin menu links use `href="@(tenantPrefix)/Admin/{Page}"`

## Status
✅ Login redirect preserves tenant context
✅ Menu navigation preserves tenant context  
✅ Authorization has correct tenant context
✅ Build successful
✅ Ready for testing

**Test it now with the steps above!**
