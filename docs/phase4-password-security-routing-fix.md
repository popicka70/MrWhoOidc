# Password and Security Pages - Tenant Routing Fix

**Date:** October 6, 2025  
**Issue:** Password and Security pages returning 404 errors when accessed via tenant-prefixed URLs  
**Status:** ✅ FIXED

## Problem

After fixing the Account pages routing, the Password and Security pages were still failing with 404 errors when accessed via:
```
https://localhost:8443/t/pop-app/Password
https://localhost:8443/t/pop-app/Mfa
```

### Root Cause Analysis

Found **three separate issues**:

#### Issue 1: Hardcoded Route Override in Mfa Page
The `/Mfa/Index.cshtml` page had a hardcoded route directive:
```cshtml
@page "/Mfa"
```

This overrides the automatic tenant-prefixed routing convention, preventing the page from being accessible at `/t/{slug}/Mfa`.

#### Issue 2: Incorrect Navigation Links
The sidebar navigation and Account pages were linking to **non-existent page locations**:
```cshtml
<!-- Wrong - these pages don't exist -->
<a href="/Account/Password">Password</a>
<a href="/Account/Security">Security</a>

<!-- Correct - actual page locations -->
<a href="/Password">Password</a>
<a href="/Mfa">Security</a>
```

The Password and MFA pages are **separate from the Account portal** and live in their own folders:
- Password page: `/Pages/Password/Index.cshtml` → URL: `/Password`
- Security (MFA) page: `/Pages/Mfa/Index.cshtml` → URL: `/Mfa`

#### Issue 3: Routing Conventions Already Configured
Good news: The `/Password` and `/Mfa` folders were **already registered** for tenant-prefixed routing in `LocalizationAndMvcExtensions.cs`:
```csharp
options.Conventions.AddFolderRouteModelConvention("/Mfa", model => AddTenantPrefixedRoutes(model));
options.Conventions.AddFolderRouteModelConvention("/Password", model => AddTenantPrefixedRoutes(model));
```

So the routing infrastructure was in place, but the hardcoded route and incorrect links were breaking it.

## Solution

### 1. Remove Hardcoded Route from Mfa Page

**File:** `MrWhoOidc.WebAuth/Pages/Mfa/Index.cshtml`

**Before:**
```cshtml
@page "/Mfa"
@model MrWhoOidc.WebAuth.Pages.Mfa.IndexModel
```

**After:**
```cshtml
@page
@model MrWhoOidc.WebAuth.Pages.Mfa.IndexModel
```

This allows the automatic tenant-prefixed routing convention to work, creating routes:
- `/Mfa` (original)
- `/t/{slug}/Mfa` (tenant-prefixed)

### 2. Update Sidebar Navigation Links

**File:** `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml`

**Before:**
```cshtml
<a href="@(tenantPrefix)/Account/Password"><i class="bi bi-key me-2"></i>Password</a>
<a href="@(tenantPrefix)/Account/Security"><i class="bi bi-shield-lock me-2"></i>Security</a>
```

**After:**
```cshtml
<a href="@(tenantPrefix)/Password"><i class="bi bi-key me-2"></i>Password</a>
<a href="@(tenantPrefix)/Mfa"><i class="bi bi-shield-lock me-2"></i>Security</a>
```

### 3. Update Account Pages Internal Links

Fixed links in all Account pages that reference Password and Security:

#### Account Dashboard (`Index.cshtml`)
**Before:**
```cshtml
<a href="@(tenantPrefix)/Account/Security" class="btn btn-sm btn-outline-primary">Manage MFA</a>
<a href="@(tenantPrefix)/Account/Password" class="btn btn-sm btn-outline-secondary">Change Password</a>
```

**After:**
```cshtml
<a href="@(tenantPrefix)/Mfa" class="btn btn-sm btn-outline-primary">Manage MFA</a>
<a href="@(tenantPrefix)/Password" class="btn btn-sm btn-outline-secondary">Change Password</a>
```

#### Account Profile Sidebar (`Profile.cshtml`)
**Before:**
```cshtml
<a href="@(tenantPrefix)/Account/Password" class="list-group-item">Change Password</a>
<a href="@(tenantPrefix)/Account/Security" class="list-group-item">Security Settings</a>
```

**After:**
```cshtml
<a href="@(tenantPrefix)/Password" class="list-group-item">Change Password</a>
<a href="@(tenantPrefix)/Mfa" class="list-group-item">Security Settings</a>
```

#### Sessions Security Notice (`Sessions.cshtml`)
**Before:**
```cshtml
<a href="@(tenantPrefix)/Account/Password" class="btn btn-sm btn-outline-warning">Change Password</a>
<a href="@(tenantPrefix)/Account/Security" class="btn btn-sm btn-outline-primary">Enable MFA</a>
```

**After:**
```cshtml
<a href="@(tenantPrefix)/Password" class="btn btn-sm btn-outline-warning">Change Password</a>
<a href="@(tenantPrefix)/Mfa" class="btn btn-sm btn-outline-primary">Enable MFA</a>
```

#### Account Tabs (`_AccountTabs.cshtml`)
**Before:**
```cshtml
<a href="@(tenantPrefix)/Account/Password">Password</a>
<a href="@(tenantPrefix)/Account/Security">Security</a>
```

**After:**
```cshtml
<a href="@(tenantPrefix)/Password">Password</a>
<a href="@(tenantPrefix)/Mfa">Security</a>
```

## Files Modified

### Core Changes (2 files):
1. **Pages/Mfa/Index.cshtml** - Removed hardcoded `/Mfa` route directive
2. **Pages/Shared/_Layout.cshtml** - Fixed sidebar navigation links

### Account Pages Link Updates (4 files):
3. **Pages/Account/Index.cshtml** - Dashboard security card links
4. **Pages/Account/Profile.cshtml** - Related settings sidebar links
5. **Pages/Account/Sessions.cshtml** - Security notice action buttons
6. **Pages/Account/_AccountTabs.cshtml** - Tab navigation links

**Total:** 6 files modified

## Architecture Understanding

### Page Structure vs. URL Structure

**Physical File Structure:**
```
Pages/
├── Account/           # Account self-service portal
│   ├── Index.cshtml       → /Account (or /t/{slug}/Account)
│   ├── Profile.cshtml     → /Account/Profile
│   └── Sessions.cshtml    → /Account/Sessions
├── Password/          # Standalone password management
│   └── Index.cshtml       → /Password (or /t/{slug}/Password)
└── Mfa/               # Standalone MFA management
    └── Index.cshtml       → /Mfa (or /t/{slug}/Mfa)
```

**Key Insight:** Password and MFA are **separate standalone pages**, not part of the Account portal folder structure. They exist at the root level of Pages and get their own routes.

### Tenant-Prefixed Routing Flow

1. **Route Convention Registration** (in `LocalizationAndMvcExtensions.cs`):
   ```csharp
   options.Conventions.AddFolderRouteModelConvention("/Password", model => AddTenantPrefixedRoutes(model));
   options.Conventions.AddFolderRouteModelConvention("/Mfa", model => AddTenantPrefixedRoutes(model));
   options.Conventions.AddFolderRouteModelConvention("/Account", model => AddTenantPrefixedRoutes(model));
   ```

2. **Automatic Route Generation:**
   - Original route: `/Password`
   - Tenant-prefixed: `/t/{slug}/Password`
   - Both work simultaneously!

3. **Tenant Resolution Middleware:**
   - Parses `/t/{slug}` from path
   - Loads tenant from database
   - Makes available via `ITenantAccessor`

### Why Hardcoded Routes Break This

When you specify `@page "/Mfa"`, you're telling Razor Pages:
- "Only respond to requests for exactly `/Mfa`"
- This **overrides** the automatic routing conventions
- Tenant-prefixed routes are **never created**

Solution: Use `@page` (no parameter) to let conventions handle route generation.

## Testing

### Verified URLs Now Work:
- ✅ `https://localhost:8443/t/pop-app/Password`
- ✅ `https://localhost:8443/t/pop-app/Mfa`
- ✅ Backward compatible: `https://localhost:8443/Password`
- ✅ Backward compatible: `https://localhost:8443/Mfa`

### All Navigation Links Fixed:
- ✅ Sidebar "My Account" → Password (now correct)
- ✅ Sidebar "My Account" → Security/MFA (now correct)
- ✅ Account Dashboard → Security card → Manage MFA (now correct)
- ✅ Account Dashboard → Security card → Change Password (now correct)
- ✅ Account Profile → Related Settings → Password (now correct)
- ✅ Account Profile → Related Settings → Security (now correct)
- ✅ Account Sessions → Security Notice → Change Password (now correct)
- ✅ Account Sessions → Security Notice → Enable MFA (now correct)
- ✅ Account Tabs → Password tab (now correct)
- ✅ Account Tabs → Security tab (now correct)

### Cross-Page Navigation Flow:
1. User logs in → redirected to `/t/pop-app/`
2. Click "My Account" → Dashboard → `/t/pop-app/Account`
3. Click "Change Password" → `/t/pop-app/Password` ✅ WORKS
4. Click "Security" in sidebar → `/t/pop-app/Mfa` ✅ WORKS
5. From Sessions page → "Change Password" → `/t/pop-app/Password` ✅ WORKS
6. All links preserve tenant context ✅ WORKS

## Build & Deploy

```powershell
# Build successful
dotnet build
# Output: Build succeeded in 7.8s with 1 pre-existing warning

# Deploy successful
docker compose up -d --build
# Output: All containers healthy and running in 18.5s
```

## Design Decisions

### Why Not Move Password/Mfa Into Account Folder?

**Option 1 (Current):** Keep separate
```
/Password          → Password management
/Mfa               → MFA management
/Account/*         → Account portal pages
```

**Pros:**
- Backward compatible with existing deployments
- Shorter URLs (`/Password` vs `/Account/Password`)
- Clear separation of concerns
- Existing authentication flows reference these paths

**Option 2 (Alternative):** Consolidate into Account
```
/Account/Password  → Password management
/Account/Security  → MFA management
/Account/*         → All other account pages
```

**Pros:**
- More organized folder structure
- Unified account portal
- All user-facing pages in one location

**Decision:** Kept separate (Option 1) because:
1. Minimizes changes to existing authentication flows
2. Maintains URL backward compatibility
3. These pages are accessed from multiple contexts (not just account portal)
4. Clear that they're standalone authentication features

### Future Consideration
If/when Phase 4 is fully complete and all pages are in `/Account/*`, could migrate Password and Mfa into the Account folder and add redirects from old URLs for compatibility.

## Lessons Learned

### 1. Don't Override Automatic Routing Without Good Reason
Hardcoded `@page "/SomePath"` directives:
- ❌ Prevent automatic tenant-prefixed route generation
- ❌ Break multi-tenant navigation
- ❌ Require manual route management

Better approach:
- ✅ Use `@page` (no parameter)
- ✅ Let folder conventions handle routing
- ✅ Tenant-prefixed routes generated automatically

### 2. Understand Physical vs. Logical Page Structure
Just because pages are **logically related** (Password, MFA, Account all user-facing) doesn't mean they're in the **same folder structure**. Check actual file locations before creating links.

### 3. Test Navigation Flows, Not Just Direct Access
It's not enough to test:
- ✅ Can I access `/t/{slug}/Password` directly?

Must also test:
- ✅ Can I navigate FROM Account Dashboard TO Password page?
- ✅ Do all internal links preserve tenant context?
- ✅ Does the full user journey work end-to-end?

### 4. Routing Conventions Are Powerful
One line of configuration:
```csharp
options.Conventions.AddFolderRouteModelConvention("/Password", model => AddTenantPrefixedRoutes(model));
```

Automatically handles:
- Route generation (`/Password` + `/t/{slug}/Password`)
- Tenant context resolution
- Backward compatibility
- All pages in the folder

Much better than manual route management!

## Related Documentation
- See `phase4-404-fix.md` for Account pages routing fix
- See `admin-ui-tenant-separation-analysis.md` for tenant routing patterns
- See `multitenant-routing-implementation.md` for routing architecture
- See `phase4-user-self-service-portal-implementation.md` for Account portal overview

## Status
✅ **FIXED** - Password and Mfa pages now use tenant-aware routing  
✅ **TESTED** - All navigation links verified working with tenant prefix  
✅ **DEPLOYED** - Changes live in Docker container  
✅ **DOCUMENTED** - Architecture decisions and troubleshooting guide complete

---

**Build Time:** 7.8 seconds  
**Deploy Time:** 18.5 seconds  
**Total Files Modified:** 6  
**Core Issue:** Hardcoded route override + incorrect link paths  
**Solution Complexity:** Simple (remove hardcoded route + update links)
