# Identity Provider Pages - Tenant-Aware URL Fix

**Date**: October 11, 2025  
**Status**: ✅ Complete  
**Impact**: Multi-Tenant Navigation

---

## Problem

When logged in as a tenant admin in a multi-tenant instance, clicking the "Add Provider" button (and other navigation links) was navigating to non-tenant-aware URLs, causing:

- **Access Denied errors** - URLs like `/Admin/Providers/Add` instead of `/t/{tenant}/Admin/Providers/Add`
- **Broken navigation** - All buttons using `asp-page` helper
- **Poor UX** - Users getting errors instead of proper functionality

### Example Issue

```
User at: /t/default/Admin/Providers
Clicks: "Add Provider"
Goes to: /Admin/Providers/Add  ❌ (non-tenant URL)
Should go: /t/default/Admin/Providers/Add  ✅ (tenant-aware URL)
```

---

## Root Cause

The Provider admin pages were using ASP.NET Core's `asp-page` tag helper, which generates URLs without tenant context:

```html
<!-- WRONG: Non-tenant-aware -->
<a asp-page="Add" class="btn btn-primary">Add Provider</a>

<!-- CORRECT: Tenant-aware -->
<a href="@(tenantPrefix)/Admin/Providers/Add" class="btn btn-primary">Add Provider</a>
```

The `asp-page` helper doesn't understand the custom tenant routing pattern `/t/{tenant-slug}/...`

---

## Solution

Replaced all `asp-page` tag helpers with manual `href` URLs that include the `tenantPrefix` variable, following the pattern used in other admin pages (Users, Realms, Scopes).

### Pattern Applied

```csharp
@inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
@inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions

@{
    var currentTenant = TenantAccessor.CurrentTenant;
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
}

<!-- Use tenantPrefix in all navigation -->
<a href="@(tenantPrefix)/Admin/Providers/Add" class="btn btn-primary">Add Provider</a>
```

---

## Files Modified

### 1. **Index.cshtml** (Providers List)
- ✅ Added tenant context injection
- ✅ Fixed "Add Provider" button
- ✅ Fixed "Delete" button in provider list

**Changes:**
```diff
- <a asp-page="Add" class="btn btn-primary">Add Provider</a>
+ <a href="@(tenantPrefix)/Admin/Providers/Add" class="btn btn-primary">Add Provider</a>

- <a asp-page="Delete" asp-route-id="@p.Id" class="btn btn-outline-danger">Del</a>
+ <a href="@(tenantPrefix)/Admin/Providers/Delete/@p.Id" class="btn btn-outline-danger">Del</a>
```

### 2. **Add.cshtml** (Add Provider)
- ✅ Added tenant context injection
- ✅ Fixed "Cancel" button

**Changes:**
```diff
+ @inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
+ @inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions

- <a asp-page="Index" class="btn btn-secondary">Cancel</a>
+ <a href="@(tenantPrefix)/Admin/Providers" class="btn btn-secondary">Cancel</a>
```

### 3. **Edit.cshtml** (Edit Provider)
- ✅ Added tenant context injection
- ✅ Fixed "Back to list" button

**Changes:**
```diff
+ @inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
+ @inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions

- <a asp-page="Index" class="btn btn-secondary">Back to list</a>
+ <a href="@(tenantPrefix)/Admin/Providers" class="btn btn-secondary">Back to list</a>
```

### 4. **Delete.cshtml** (Delete Provider)
- ✅ Added tenant context injection
- ✅ Fixed "Cancel" button

**Changes:**
```diff
+ @inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
+ @inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions

- <a asp-page="Index" class="btn btn-secondary">Cancel</a>
+ <a href="@(tenantPrefix)/Admin/Providers" class="btn btn-secondary">Cancel</a>
```

---

## Testing Scenarios

### Multi-Tenant Mode (Enabled)
✅ **Navigate from** `/t/acme-corp/Admin/Providers`
✅ **Click "Add Provider"** → Goes to `/t/acme-corp/Admin/Providers/Add`
✅ **Click "Cancel"** → Returns to `/t/acme-corp/Admin/Providers`
✅ **Click "Edit"** → Goes to `/t/acme-corp/Admin/Providers/Edit/{id}`
✅ **Click "Delete"** → Goes to `/t/acme-corp/Admin/Providers/Delete/{id}`

### Single-Tenant Mode (Disabled)
✅ **Navigate from** `/Admin/Providers`
✅ **Click "Add Provider"** → Goes to `/Admin/Providers/Add`
✅ **All buttons work** without `/t/{tenant}` prefix

---

## Consistency with Other Admin Pages

This fix aligns Identity Provider pages with the existing pattern used in:

| Page Section | Pattern |
|--------------|---------|
| Users | ✅ Uses `href="@(tenantPrefix)/Admin/Users/..."` |
| Roles | ✅ Uses `href="@(tenantPrefix)/Admin/Roles/..."` |
| Realms | ✅ Uses `href="@(tenantPrefix)/Admin/Realms/..."` |
| Scopes | ✅ Uses `href="@(tenantPrefix)/Admin/Scopes/..."` |
| **Providers** | ✅ **NOW** uses `href="@(tenantPrefix)/Admin/Providers/..."` |

---

## Why `asp-page` Doesn't Work

The `asp-page` tag helper is designed for standard Razor Pages routing, which follows:
```
/PageFolder/PageName
```

But multi-tenant routing uses a custom pattern:
```
/t/{tenant-slug}/PageFolder/PageName
```

The tag helper doesn't understand this custom routing convention, so it generates incorrect URLs.

### Workaround Options

1. **Manual href** (chosen solution) - Direct control, works reliably
2. **Custom tag helper** - Complex, requires maintenance
3. **Middleware rewriting** - Complex, affects all routes
4. **Areas routing** - Doesn't support dynamic tenant slugs

---

## Future Considerations

### Pages Still Using `asp-page` (Forms)

Some pages still use `asp-page` for form posts and button handlers:
- `asp-page-handler="Upload"` - Form handler for uploads
- `asp-page-handler="Test"` - Test connection button
- `method="post" asp-page=""` - Form post to current page

**These are OK** because:
- Form posts to the same page work correctly
- Page handlers are processed server-side
- Only navigation links need `tenantPrefix`

### When to Use Each Approach

| Scenario | Use |
|----------|-----|
| Navigation links | `href="@(tenantPrefix)/Admin/..."` |
| Form posts (same page) | `<form method="post">` |
| Form handlers | `asp-page-handler="HandlerName"` |
| External redirects | Server-side `RedirectToPage` with tenant logic |

---

## Build & Deploy

```powershell
# Build
dotnet build

# Rebuild Docker containers
docker compose up -d --build

# Test
# 1. Login as tenant admin at: https://localhost:8443/t/{tenant}/login
# 2. Navigate to: https://localhost:8443/t/{tenant}/Admin/Providers
# 3. Click "Add Provider" - should stay in tenant context
```

---

## Related Issues & Fixes

This is part of a larger fix for Identity Provider tenant administration:

1. ✅ **Authorization Policy** - Changed from "admin" to "tenant-admin"
2. ✅ **Tenant Filtering** - Added to Admin API endpoints
3. ✅ **Tenant Assignment** - Auto-assign providers to current tenant
4. ✅ **AccessDenied Page** - Created missing error page
5. ✅ **Navigation URLs** - Fixed tenant-aware routing (THIS FIX)

---

## References

- **Pattern Source**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml`
- **Tenant Routing**: Custom middleware in `Program.cs`
- **Multi-Tenancy**: `MrWhoOidc.Auth.MultiTenancy` namespace
- **Related Fix**: `docs/identity-provider-admin-api-tenant-filtering-fix.md`
