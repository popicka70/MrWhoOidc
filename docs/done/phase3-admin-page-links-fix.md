# Phase 3: Admin Page Tenant-Aware Links Fix

## Problem: All Edit Buttons Use Platform-Level Links

### Symptoms
After implementing tenant-aware navigation in the layout, all "Edit" buttons and other links within admin pages were still using platform-level paths like `/Admin/Clients/Edit/...` instead of tenant-aware paths like `/t/pop-app/Admin/Clients/Edit/...`.

This caused clicking Edit to lose tenant context and fail authorization.

### Root Cause

All admin pages were using ASP.NET Core Tag Helpers (`asp-page`, `asp-route-id`) which generate platform-level absolute paths:

```razor
<!-- BEFORE (broken) -->
<a asp-page="Edit" asp-route-id="@c.Id" class="btn btn-outline-secondary">
    Edit
</a>
<!-- Generates: /Admin/Clients/Edit/{id} ❌ -->
```

Tag helpers don't know about the tenant context from `ITenantAccessor`, so they always generate non-tenant-aware URLs.

## The Solution

### Strategy: Manual href with ITenantAccessor

Replace all `asp-page` tag helpers with manual `href` attributes that use `ITenantAccessor` to build the tenant prefix, following the same pattern used in `_Layout.cshtml`:

```razor
<!-- AFTER (fixed) -->
@inject ITenantAccessor TenantAccessor
@inject IOptions<MultiTenancyOptions> MultiTenancyOptions
@{
    var currentTenant = TenantAccessor.CurrentTenant;
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
}

<a href="@(tenantPrefix)/Admin/Clients/Edit/@c.Id" class="btn btn-outline-secondary">
    Edit
</a>
<!-- Generates: /t/pop-app/Admin/Clients/Edit/{id} ✅ -->
```

## Files Modified

### Admin Index Pages (List Views)

All index pages now inject `ITenantAccessor` and build `tenantPrefix`:

1. **Clients/Index.cshtml**
   - Edit button: `/t/{slug}/Admin/Clients/Edit/{id}`
   - Add Client button: `/t/{slug}/Admin/Clients/Add`

2. **Users/Index.cshtml**
   - Edit button: `/t/{slug}/Admin/Users/Edit/{id}`

3. **Realms/Index.cshtml**
   - Edit button: `/t/{slug}/Admin/Realms/Edit/{id}`

4. **Roles/Index.cshtml**
   - Edit button: `/t/{slug}/Admin/Roles/Edit/{id}`

5. **Scopes/Index.cshtml**
   - Edit button: `/t/{slug}/Admin/Scopes/Edit/{name}`

6. **Providers/Index.cshtml**
   - View button: `/t/{slug}/Admin/Providers/Details/{id}`
   - Edit button: `/t/{slug}/Admin/Providers/Edit/{id}`
   - Claims button: `/t/{slug}/Admin/Providers/ClaimMappings/{id}`
   - Keys button: `/t/{slug}/Admin/ProviderKeys/Index?providerId={id}`

### Shared Components

7. **Users/_UserTabs.cshtml** (User detail tab navigation)
   - Profile: `/t/{slug}/Admin/Users/Edit/{id}`
   - Emails: `/t/{slug}/Admin/Users/Emails/Index?userId={id}`
   - Clients: `/t/{slug}/Admin/Users/Clients/Index?userId={id}`
   - Roles: `/t/{slug}/Admin/Users/Roles/Index?userId={id}`
   - Linked accounts: `/t/{slug}/Admin/Users/Linked/Index?userId={id}`

### Detail/Edit Pages with Cross-References

8. **Providers/Details.cshtml**
   - Manage claim mappings: `/t/{slug}/Admin/Providers/ClaimMappings/{id}`
   - Edit: `/t/{slug}/Admin/Providers/Edit/{id}`
   - Manage keys: `/t/{slug}/Admin/ProviderKeys/Index?providerId={id}`

9. **Clients/Edit.cshtml**
   - Open full mapping page: `/t/{slug}/Admin/ProviderMappings/Index`
   - Open dedicated keys page: `/t/{slug}/Admin/ClientKeys/Index/{clientId}`

10. **ClientKeys/Index.cshtml**
    - Back to client: `/t/{slug}/Admin/Clients/Edit/{id}`

11. **ProviderKeys/Index.cshtml**
    - Back: `/t/{slug}/Admin/Providers/Index`

12. **ProviderClaimMappings/Index.cshtml**
    - Edit: `/t/{slug}/Admin/ProviderClaimMappings/Edit/{id}`

## Implementation Pattern

### Step 1: Inject Dependencies
```razor
@page
@inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
@inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions
@model YourPageModel
```

### Step 2: Build Tenant Prefix
```razor
@{
    ViewData["Title"] = "Page Title";
    var currentTenant = TenantAccessor.CurrentTenant;
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
}
```

### Step 3: Use Manual href
```razor
<!-- Single route parameter -->
<a href="@(tenantPrefix)/Admin/Entity/Edit/@entity.Id">Edit</a>

<!-- Query string parameter -->
<a href="@(tenantPrefix)/Admin/Entity/Index?parentId=@parent.Id">View</a>

<!-- Multiple segments -->
<a href="@(tenantPrefix)/Admin/Users/Clients/Index?userId=@user.Id">Clients</a>
```

## Why This Works

1. **Per-Request Context**: `ITenantAccessor.CurrentTenant` is set by `TenantResolutionMiddleware` for each request
2. **Consistent with Layout**: Uses identical pattern to `_Layout.cshtml` navigation
3. **Multi-Tenancy Aware**: Checks `MultiTenancyOptions.Enabled` before adding prefix
4. **Fallback Support**: Empty prefix for single-tenant mode or platform-level pages

## Testing Verification

### Before Fix
```
1. Login as tenant admin: admin@pop.app
2. Go to: https://localhost:8443/t/pop-app/Admin/Clients
3. Click "Edit" on any client
4. URL becomes: https://localhost:8443/Admin/Clients/Edit/{id} ❌
5. Middleware: No /t/{slug}/ → default tenant
6. Authorization: Fails (wrong tenant context)
7. Result: Access Denied
```

### After Fix
```
1. Login as tenant admin: admin@pop.app
2. Go to: https://localhost:8443/t/pop-app/Admin/Clients
3. Click "Edit" on any client
4. URL becomes: https://localhost:8443/t/pop-app/Admin/Clients/Edit/{id} ✅
5. Middleware: Resolves tenant = pop-app
6. Authorization: Succeeds (correct tenant context)
7. Result: Edit page loads successfully
```

## Complete Navigation Flow

```
User Login
    ↓
/t/pop-app/ (home)
    ↓
/t/pop-app/Admin/Clients (clients list) ✅
    ↓
Click Edit
    ↓
/t/pop-app/Admin/Clients/Edit/{id} ✅
    ↓
Click "Open dedicated keys page"
    ↓
/t/pop-app/Admin/ClientKeys/Index/{id} ✅
    ↓
Click "Back to client"
    ↓
/t/pop-app/Admin/Clients/Edit/{id} ✅
```

**All paths maintain `/t/{slug}/` prefix → tenant context preserved → authorization works!**

## Alternative Solutions Considered

### ❌ Option 1: Custom Tag Helper
Create custom `tenant-asp-page` tag helper that extends `asp-page`.

**Rejected**: Too complex, requires extensive testing, affects all pages.

### ❌ Option 2: Middleware URL Rewriting
Rewrite all `/Admin/...` URLs to `/t/{slug}/Admin/...` in middleware.

**Rejected**: Fragile, loses explicit control, harder to debug.

### ✅ Option 3: Manual href with ITenantAccessor (Chosen)
Use `ITenantAccessor` to build tenant prefix explicitly.

**Benefits**:
- Explicit and clear
- Consistent with layout pattern
- Easy to understand and maintain
- Works for all link types (navigation, forms, buttons)

## Summary

Fixed all admin page navigation by replacing ASP.NET Core tag helpers (`asp-page`, `asp-route-*`) with manual `href` attributes that use `ITenantAccessor` to build tenant-aware URLs.

**Changed files**: 12 admin pages across Clients, Users, Realms, Roles, Scopes, Providers, and their detail/edit pages.

**Result**: All Edit buttons, View buttons, and cross-references now preserve tenant context, ensuring proper authorization and user experience.

## Related Fixes

This completes the Phase 3 tenant-aware navigation chain:

1. ✅ **Layout Navigation** - Menu links use ITenantAccessor
2. ✅ **Login Redirect** - Redirects to `/t/{slug}/`
3. ✅ **Cookie Auth Redirects** - Access denied preserves tenant
4. ✅ **Role Seeding** - Creates `tenant-admin` in `default` realm
5. ✅ **EF Core FK Fix** - Batch client creation before role assignments
6. ✅ **Admin Page Links** - All Edit/View/navigation buttons tenant-aware

**Phase 3 is now fully operational with complete tenant isolation! 🎉**
