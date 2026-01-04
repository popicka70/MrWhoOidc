# Tenant-Aware Redirect Fix - Implementation Summary

## Date: October 11, 2025

## Problem
`RedirectToPage()` helper in Razor Pages doesn't understand `/t/{tenant}/` routing patterns, causing form submissions to redirect users to the wrong tenant (e.g., from `/t/pop-app` to `/t/default`).

## Solution Overview
1. Created `TenantAwarePageModel` base class with helper methods
2. Updated affected page models to inherit from this base class
3. Replaced `RedirectToPage()` calls with `TenantAwareRedirect()` helper

## Files Created

### TenantAwarePageModel.cs
**Location:** `MrWhoOidc.WebAuth/Pages/Admin/TenantAwarePageModel.cs`

**Purpose:** Base class for admin pages providing tenant-aware redirect helpers.

**Key Methods:**
- `TenantAwareRedirect(string pagePath, object? routeValues = null)` - Redirects to a page within current tenant context
- `TenantAwareRedirectToPage()` - Redirects to current page (POST-Redirect-GET pattern)

## Files Fixed

### Providers Section (COMPLETE)
- ✅ `Add.cshtml.cs` - 1 redirect fixed
- ✅ `Edit.cshtml.cs` - 1 redirect fixed
- ✅ `Delete.cshtml.cs` - 2 redirects fixed
- ✅ `ClaimMappings.cshtml.cs` - 5 redirects fixed, added helper method

### Realms Section (COMPLETE)
- ✅ `Add.cshtml.cs` - 1 redirect fixed
- ✅ `Edit.cshtml.cs` - 1 redirect fixed
- ✅ `Index.cshtml.cs` - 2 redirects fixed

### Users Section (PARTIAL)
- ✅ `UserPageModelBase.cs` - Updated to inherit from TenantAwarePageModel
- ✅ `Add.cshtml.cs` - 1 redirect fixed
- ✅ `Index.cshtml.cs` - 4 redirects fixed
- ✅ `Edit.cshtml.cs` - 4 redirects fixed
- ✅ `Roles/Index.cshtml.cs` - Constructor fixed (redirects TODO)
- ✅ `Linked/Index.cshtml.cs` - Constructor fixed (redirects TODO)
- ✅ `Clients/Index.cshtml.cs` - Constructor fixed (redirects TODO)
- ✅ `Emails/Index.cshtml.cs` - Constructor fixed (redirects TODO)

### Clients Section (COMPLETE)
- ✅ `Add.cshtml.cs` - 1 redirect fixed
- ✅ `Edit.cshtml.cs` - 5 redirects fixed
- ✅ `Index.cshtml.cs` - 3 redirects fixed

### Roles Section (COMPLETE)
- ✅ `Add.cshtml.cs` - 1 redirect fixed
- ✅ `Edit.cshtml.cs` - 3 redirects fixed
- ✅ `Index.cshtml.cs` - 3 redirects fixed

### Scopes Section (COMPLETE)
- ✅ `Add.cshtml.cs` - 1 redirect fixed (platform-admin only)
- ✅ `Edit.cshtml.cs` - 3 redirects fixed (platform-admin only)
- ✅ `Index.cshtml.cs` - 5 redirects fixed (view for all, delete for platform-admin)

## Remaining Work

### Files Still Needing Fixes

Based on grep search, the following admin sections have `RedirectToPage()` calls that need fixing:

1. **Users Sub-Pages** (~18 redirects across Roles, Linked, Clients, Emails pages)
2. **Settings** (1 file)
3. **Registrations** (Index.cshtml.cs)

**Estimated Total:** ~20-25 additional redirects need fixing

## Migration Strategy

### For Each Admin Section:

1. **Update constructor to inject ITenantAccessor:**
   ```csharp
   public class SomeModel(
       AuthDbContext db,
       ITenantAccessor tenantAccessor) : TenantAwarePageModel(tenantAccessor)
   ```

2. **Replace RedirectToPage calls:**
   ```csharp
   // OLD:
   return RedirectToPage("Index");
   return RedirectToPage("Index", new { TenantId });
   return RedirectToPage();
   
   // NEW:
   return TenantAwareRedirect("/Admin/SomeSection");
   return TenantAwareRedirect("/Admin/SomeSection", new { TenantId });
   return TenantAwareRedirectToPage();
   ```

3. **For relative page redirects:**
   - `RedirectToPage("Index")` → `TenantAwareRedirect("/Admin/[Section]")`
   - `RedirectToPage("Edit", new { id })` → `TenantAwareRedirect("/Admin/[Section]/Edit/{id}")`
   - `RedirectToPage()` → `TenantAwareRedirectToPage()`

## Testing

All 366 tests currently passing with the fixes applied so far.

## Pattern Reference

### Simple Redirect
```csharp
// OLD
return RedirectToPage("Index");

// NEW
return TenantAwareRedirect("/Admin/Users");
```

### Redirect with Route Values
```csharp
// OLD
return RedirectToPage("Index", new { TenantId = Input.TenantId });

// NEW
return TenantAwareRedirect("/Admin/Users", new { TenantId = Input.TenantId });
```

### Redirect to Current Page
```csharp
// OLD
return RedirectToPage();

// NEW
return TenantAwareRedirectToPage();
```

### Conditional Redirects
```csharp
// OLD
if (entity is null) return RedirectToPage("Index");

// NEW
if (entity is null) return TenantAwareRedirect("/Admin/Users");
```

## Known Issues

### CS9107 Warning
The following warning appears when passing tenantAccessor to both constructor and base:
```
Parameter 'ITenantAccessor tenantAccessor' is captured into the state of the enclosing type 
and its value is also passed to the base constructor.
```

This is a C# 12 informational warning and is safe to ignore. The parameter is intentionally 
passed to the base class for use in redirect helpers while also being available to the derived class.

## Next Steps

1. Create automated script to fix remaining files systematically
2. Run full test suite after all changes
3. Manual testing of each admin section
4. Update documentation for new base class usage
5. Consider creating analyzer rule to prevent future `RedirectToPage` usage in admin pages

## Middleware Analysis

The existing `TenantResolutionMiddleware` already has protection (lines 133-189) that detects 
when an authenticated user tries to access a tenant they don't belong to and redirects them.

However, this middleware can't intercept redirects generated by page handlers because:
1. Page handler executes and returns redirect response
2. Browser follows redirect
3. Middleware detects cross-tenant access and redirects again

The proper fix is using explicit tenant-aware URLs, which we're implementing systematically.

## Files Requiring Special Attention

1. **Clients Pages** - May have complex routing with multiple parameters
2. **Settings Page** - Check if it needs tenant awareness
3. **Registrations** - Verify intended behavior for registration redirects

## Success Criteria

- [ ] All `RedirectToPage()` calls in admin sections replaced
- [ ] All tests passing (currently 366/366)
- [ ] Manual testing confirms no cross-tenant navigation
- [ ] No regression in existing functionality
- [ ] Code compiles without errors (warnings OK)

