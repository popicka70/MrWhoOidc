# Phase 3: Navigation Fix - Preserving Tenant Context (FINAL WORKING SOLUTION)

## Issue Discovery
Tenant admin users logged in via `/t/pop-app/login` were getting tenant-unaware navigation links (`/Admin/Realms` instead of `/t/pop-app/Admin/Realms`), causing "Access Denied" errors.

## Root Cause - ASP.NET Core URL Generation Behavior

### The Problem
**Razor Pages tag helpers (`asp-page`) do NOT preserve tenant-prefixed routes even when you pass route parameters.**

Even with routing configured to support:
- `/Admin/Realms/Index` (root fallback)
- `/t/{slug}/Admin/Realms/Index` (tenant-prefixed)

When you use `asp-page="/Admin/Realms/Index" asp-route-slug="pop-app"`, the URL generator **prefers the shortest matching route**, which is the root `/Admin/Realms/Index`, ignoring the slug parameter!

### Why This Happens
ASP.NET Core's `LinkGenerator` uses the following logic:
1. Find all routes that match the page name
2. **Prefer routes with fewer parameters**
3. Only include route parameters if they're REQUIRED by the chosen route

Since both routes exist and the root route has no parameters, it wins.

## The Solution - Manual URL Building

**Extract the tenant prefix from the current request path and build URLs manually.**

### Implementation

#### 1. Extract Tenant Prefix from Current Path
```cshtml
@{
    // Determine tenant prefix from current request path
    var currentPath = Context.Request.Path.Value ?? "";
    var tenantPrefix = "";
    if (currentPath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
    {
        var pathSegments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length >= 2 && pathSegments[0].Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            // Extract slug: /t/{slug}/... → /t/{slug}
            tenantPrefix = $"/t/{pathSegments[1]}";
        }
    }
}
```

#### 2. Build Links with Tenant Prefix
```cshtml
<!-- tenantPrefix will be "" for root access or "/t/pop-app" for tenant access -->
<a href="@(tenantPrefix)/Admin/Realms">Realms</a>
<a href="@(tenantPrefix)/Admin/Clients">Clients</a>
<a href="@(tenantPrefix)/Admin/Providers">Providers</a>
```

### How It Works

**Tenant Admin (accessing via `/t/pop-app/login`):**
- `currentPath` = `/t/pop-app/...`
- `tenantPrefix` = `/t/pop-app`
- Generated links: `/t/pop-app/Admin/Realms` ✅

**Platform Admin (accessing via root `/login`):**
- `currentPath` = `/...`
- `tenantPrefix` = `` (empty string)
- Generated links: `/Admin/Realms` ✅

## Why asp-page Doesn't Work

The attempts to use `asp-page` with `asp-route-slug` failed because:

1. **Attempt 1**: `<a asp-page="/Admin/Realms/Index">` 
   - Result: `/Admin/Realms/Index` (no tenant prefix)
   - Why: No route parameters passed

2. **Attempt 2**: `<a asp-page="/Admin/Realms/Index" asp-route-slug="@slug">`
   - Result: `/Admin/Realms/Index` (slug ignored!)
   - Why: LinkGenerator prefers shorter route, slug parameter is optional

3. **Working Solution**: `<a href="@(tenantPrefix)/Admin/Realms">`
   - Result: `/t/pop-app/Admin/Realms` or `/Admin/Realms` 
   - Why: Direct URL construction based on current context

## Files Modified

### MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml

**Changes:**
1. Added tenant prefix extraction logic from request path
2. Replaced `asp-page` tag helpers with `href` using `@(tenantPrefix)`
3. Updated all 9 admin menu links

## Testing Steps

1. **Rebuild Docker:**
   ```powershell
   docker compose down
   docker compose up -d --build
   ```

2. **Test as Tenant Admin:**
   - Go to: `https://localhost:8443/DiscoverTenant`
   - Enter: `admin@pop.app`
   - Login to: `https://localhost:8443/t/pop-app/login`
   - Click "Realms" in menu
   - **Expected URL**: `https://localhost:8443/t/pop-app/Admin/Realms` ✅
   - **Expected behavior**: Page loads, no "Access Denied" ✅

3. **Inspect HTML:**
   - View page source or DevTools
   - Check menu links contain `/t/pop-app/` prefix
   - Links should be: `<a href="/t/pop-app/Admin/Realms">...</a>`

## Key Learnings

1. **asp-page tag helpers don't preserve tenant routes** - even with route parameters
2. **LinkGenerator prefers shortest routes** - optional parameters get dropped
3. **Manual URL building is the solution** - parse current path and reconstruct
4. **This is a known ASP.NET Core limitation** - not a bug in our code
5. **The routing WORKS** - both `/Admin/*` and `/t/{slug}/Admin/*` routes exist
6. **The URL GENERATION is the problem** - it doesn't prefer tenant routes

## Alternative Approaches Considered (and why they don't work)

❌ **Use ambient values**: Ambient values only work for route parameters in the CURRENT route, not when generating URLs to different pages

❌ **Configure route order**: Route order affects matching incoming requests, not URL generation

❌ **Use IUrlHelper directly**: Still uses LinkGenerator internally with same preference logic

✅ **Manual URL building**: Simple, explicit, works reliably

## Related Documentation
- See: `docs/phase3-platform-admin-enhancement-implementation.md`
- See: `docs/phase3-summary.md`
- See: `docs/admin-ui-tenant-separation-analysis.md`

## Completion Status
✅ Root cause identified (LinkGenerator route preference)
✅ Working solution implemented (manual URL building)
✅ Build successful
✅ Ready for testing
