# AccessDenied Page Creation

**Date**: October 11, 2025  
**Status**: ✅ Complete  
**Impact**: User Experience & Error Handling

---

## Problem

When users attempted to access resources they don't have permission for (e.g., tenant admins trying to access admin endpoints), the application tried to redirect to `/Account/AccessDenied`, but this page didn't exist, resulting in:

- **HTTP 404 Error** instead of a proper "Access Denied" message
- **Poor user experience** - confusing error instead of helpful guidance
- **Missing URL**: `https://localhost:8443/t/{tenant}/Account/AccessDenied`

---

## Root Cause

The authentication configuration in `AuthenticationAuthorizationExtensions.cs` was configured to redirect to `/Account/AccessDenied` when authorization failed, but this Razor Page was never created.

```csharp
OnRedirectToAccessDenied = context =>
{
    var accessDeniedPath = currentTenant != null && multiTenancyOptions?.Enabled == true
        ? $"/t/{currentTenant.Slug}/Account/AccessDenied"
        : "/Account/AccessDenied";
    
    context.Response.Redirect($"{accessDeniedPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
    return Task.CompletedTask;
}
```

---

## Solution

Created the missing `AccessDenied` page with proper error messaging and user-friendly navigation options.

### Files Created

1. **`MrWhoOidc.WebAuth/Pages/Account/AccessDenied.cshtml`**
   - User-friendly access denied page
   - Bootstrap styling with danger alert
   - Shows attempted URL
   - Context-aware navigation buttons

2. **`MrWhoOidc.WebAuth/Pages/Account/AccessDenied.cshtml.cs`**
   - Page model with tenant-aware URL generation
   - Handles both multi-tenant and single-tenant scenarios
   - Provides appropriate navigation options based on user authentication status

---

## Features

### Page Content

✅ **Clear Error Message**
- Explains why access was denied
- Lists common reasons (role required, wrong tenant, expired session)

✅ **Attempted URL Display**
- Shows the URL the user tried to access
- Helps users and admins diagnose permission issues

✅ **Context-Aware Navigation**
- **Home Button** - Return to tenant home
- **Dashboard Button** - For users with admin roles
- **My Account Button** - For authenticated users
- **Sign In Button** - For anonymous users

✅ **Help Section**
- Guidance for authenticated users
- Suggests contacting administrator
- Reminds users to provide the attempted URL

### Multi-Tenant Support

The page automatically adapts to the current tenant context:

```csharp
// Multi-tenant URLs
/t/{tenant-slug}/Account/AccessDenied
/t/{tenant-slug}/login
/t/{tenant-slug}/Admin/Users

// Single-tenant URLs
/Account/AccessDenied
/login
/Admin/Users
```

---

## User Experience Flow

### Before
```
User clicks "Add Provider"
     ↓
Authorization fails
     ↓
Redirect to /Account/AccessDenied
     ↓
❌ HTTP 404 Error (Page not found)
```

### After
```
User clicks "Add Provider"
     ↓
Authorization fails
     ↓
Redirect to /Account/AccessDenied
     ↓
✅ Proper access denied page with:
   - Clear error message
   - Attempted URL
   - Navigation options
   - Help guidance
```

---

## Testing

### Test Scenarios

1. ✅ **Tenant Admin accessing platform-admin resource**
   - Should show access denied page
   - Should show tenant-aware navigation

2. ✅ **Anonymous user accessing protected resource**
   - Should show access denied page
   - Should show "Sign In" button

3. ✅ **User accessing different tenant's resource**
   - Should show access denied page
   - Should explain tenant isolation

---

## Design Details

### Visual Elements

- **Danger Border** - Red border to indicate error
- **Shield Icon** - Visual indicator of access restriction
- **Alert Boxes** - Bootstrap alerts for clear messaging
- **Button Grid** - Responsive button layout
- **Info Section** - Light background for help text

### Accessibility

- Semantic HTML structure
- ARIA roles where appropriate
- Bootstrap Icons for visual cues
- Responsive design (mobile-friendly)

---

## Code Highlights

### Tenant-Aware URL Building

```csharp
var currentTenant = _tenantAccessor.CurrentTenant;
var isMultiTenant = _multiTenancyOptions.Value.Enabled && currentTenant != null;

if (isMultiTenant)
{
    var tenantPrefix = $"/t/{currentTenant!.Slug}";
    HomeUrl = tenantPrefix;
    LoginUrl = $"{tenantPrefix}/login";
    AccountUrl = $"{tenantPrefix}/Account";
}
```

### Role-Based Dashboard Link

```csharp
if (User?.IsInRole("admin") == true || User?.IsInRole("tenant-admin") == true)
{
    DashboardUrl = $"{tenantPrefix}/Admin/Users";
}
```

---

## Build & Deploy

```powershell
# Build
dotnet build

# Rebuild Docker containers
docker compose up -d --build

# Test the page
# Navigate to: https://localhost:8443/t/{tenant}/Account/AccessDenied
```

---

## Related Issues

This page is shown when:
- User lacks required role (e.g., "admin", "tenant-admin", "platform-admin")
- User tries to access another tenant's resources
- User's session has expired but they're still authenticated
- Authorization policy fails for any reason

---

## Future Enhancements

- [ ] Add detailed permission requirements display
- [ ] Show which role/claim is missing
- [ ] Add "Request Access" feature
- [ ] Log access denied events for security monitoring
- [ ] Add customizable messages per tenant
- [ ] Support for localization

---

## References

- **Authentication Config**: `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/AuthenticationAuthorizationExtensions.cs`
- **Related Pages**: `MrWhoOidc.WebAuth/Pages/Account/*.cshtml`
- **Authorization Handlers**: `MrWhoOidc.WebAuth/Security/Admin/*AuthorizationHandler.cs`
