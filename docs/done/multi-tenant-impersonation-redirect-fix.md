# Multi-Tenant Impersonation Redirect Fix

**Date:** October 6, 2025  
**Issue:** Impersonation redirect failing in multi-tenant mode  
**Status:** ✅ Fixed

## Problem

When starting impersonation, the application was redirecting to `/Admin/Index`, which doesn't exist in multi-tenant mode. In multi-tenant mode, admin pages are accessed via `/t/{slug}/Admin/Index`.

**Error:**
```
System.InvalidOperationException: No page named '/Admin/Index' matches the supplied values.
```

## Root Cause

Two pages were using hardcoded redirects without checking multi-tenant mode:
1. `StartImpersonation.cshtml.cs` - Original impersonation handler
2. `Impersonation.cshtml.cs` - New dedicated impersonation page

Both used:
```csharp
return RedirectToPage("/Admin/Index");
```

This works in **single-tenant mode** but fails in **multi-tenant mode** because the route requires a tenant slug prefix.

## Solution

Updated both pages to:
1. Check if multi-tenancy is enabled via `MultiTenancyOptions`
2. Query the database for the tenant's slug
3. Redirect to the correct URL based on mode:
   - **Single-tenant:** `/Admin/Index`
   - **Multi-tenant:** `/t/{slug}/Admin/Index`

## Changes Made

### 1. StartImpersonation.cshtml.cs

**Added Dependencies:**
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
```

**Updated Constructor:**
```csharp
public class StartImpersonationModel(
    IImpersonationService impersonationService,
    AuthDbContext db,                              // NEW
    IOptions<MultiTenancyOptions> multiTenancyOptions) : PageModel  // NEW
```

**Updated Redirect Logic:**
```csharp
// Get tenant slug for redirect
var tenantSlug = await db.Tenants
    .Where(t => t.Id == tenantId)
    .Select(t => t.Slug)
    .FirstOrDefaultAsync();

if (tenantSlug == null)
{
    TempData["Error"] = "Tenant not found.";
    return RedirectToPage("/PlatformAdmin/Tenants/Index");
}

// Redirect based on mode
if (multiTenancyOptions.Value.Enabled)
{
    return Redirect($"/t/{tenantSlug}/Admin/Index");
}
return RedirectToPage("/Admin/Index");
```

### 2. Impersonation.cshtml.cs (New Page)

**Added Dependencies:**
```csharp
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
```

**Updated Constructor:**
```csharp
public class ImpersonationModel(
    AuthDbContext db,
    IImpersonationService impersonationService,
    IOptions<MultiTenancyOptions> multiTenancyOptions) : PageModel  // NEW
```

**Updated Redirect Logic:**
```csharp
// Get tenant info for redirect
var tenant = await db.Tenants
    .Where(t => t.Id == tenantId)
    .Select(t => new { t.Name, t.Slug })
    .FirstOrDefaultAsync();

if (tenant == null)
{
    TempData["Error"] = "Tenant not found.";
    return RedirectToPage();
}

TempData["Success"] = $"Now impersonating tenant: {tenant.Name}. All write operations are disabled.";

// Redirect based on mode
if (multiTenancyOptions.Value.Enabled)
{
    return Redirect($"/t/{tenant.Slug}/Admin/Index");
}
return RedirectToPage("/Admin/Index");
```

## Testing

**Scenario 1: Multi-Tenant Mode (Current)**
1. Login as platform admin
2. Navigate to `/PlatformAdmin/Impersonation`
3. Click "Start Impersonation" on a tenant (e.g., "default")
4. **Expected:** Redirects to `/t/default/Admin/Index` ✅
5. **Expected:** Red impersonation banner appears ✅
6. **Expected:** POST requests are blocked ✅

**Scenario 2: Single-Tenant Mode**
1. Set `MultiTenancyOptions.Enabled = false`
2. Start impersonation
3. **Expected:** Redirects to `/Admin/Index` (no tenant prefix)
4. **Expected:** All other functionality works the same

## Build Status

✅ **Build Successful**
- All projects compiled
- Only 1 pre-existing warning (Scopes/Index.cshtml.cs)
- No new errors

## Impact

**Files Changed:**
- `MrWhoOidc.WebAuth/Pages/StartImpersonation.cshtml.cs`
- `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Impersonation.cshtml.cs`

**Areas Affected:**
- ✅ Platform admin impersonation workflow
- ✅ Multi-tenant routing
- ✅ No impact on existing functionality

## Related Issues

This fix also addresses a potential issue in the existing `StartImpersonation` page that would have occurred when multi-tenancy is enabled.

## Additional Notes

### Why Use `Redirect()` Instead of `RedirectToPage()`?

In multi-tenant mode, admin pages are not direct Razor Pages routes. The route is dynamically constructed:
- `/t/{slug}/Admin/Index` → Maps to `/Admin/Index` page with tenant context

Using `Redirect()` with the full path ensures the URL is correctly formed for the multi-tenant middleware to resolve.

### Alternative Approaches Considered

1. **Use ITenantAccessor:** 
   - ❌ Not available at this point because impersonation hasn't set the tenant context yet
   - The impersonation service sets the session, but TenantResolutionMiddleware runs on the next request

2. **Modify ImpersonationService to return redirect URL:**
   - ✅ Could work but requires more changes
   - Current solution is simpler and more explicit

3. **Use TempData to pass tenant slug:**
   - ❌ Unnecessary complexity
   - Querying the database is straightforward and reliable

## Future Enhancements

Consider adding a helper method to `IImpersonationService`:
```csharp
Task<string> GetAdminDashboardUrlAfterImpersonation(Guid tenantId);
```

This would centralize the URL construction logic and make it easier to maintain.

## Documentation Updated

- **Impersonation Page Documentation:** `docs/impersonation-page-complete.md`
- **Testing Guide:** `docs/phase5b-feature4-testing-guide.md`
- **This Fix Summary:** `docs/multi-tenant-impersonation-redirect-fix.md`
