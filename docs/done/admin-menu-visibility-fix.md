# Admin Menu Visibility Fix

## Problem

The Admin section in the navigation menu was visible to all authenticated users, regardless of whether they had tenant-admin or platform-admin privileges. This created a poor user experience where regular users would see admin menu items they couldn't access.

## Solution

Modified `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` to wrap the Admin section in an authorization check, similar to how the Platform Admin section is handled.

## Changes Made

### File: `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml`

**Before:** Lines 217-231

```cshtml
<div class="list-group-item fw-semibold text-uppercase small bg-success text-white">Admin</div>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Admin/Realms">...</a>
<!-- ... more admin links ... -->
```

**After:** Lines 217-248

```cshtml
@* Admin Section - Only visible to tenant admins or platform admins *@
@if (User?.Identity?.IsAuthenticated ?? false)
{
    var tenantAdminResult = await AuthorizationService.AuthorizeAsync(User, null, "tenant-admin");
    var platformAdminCheck = await AuthorizationService.AuthorizeAsync(User, null, "platform-admin");
    if (tenantAdminResult.Succeeded || platformAdminCheck.Succeeded)
    {
        <div class="list-group-item fw-semibold text-uppercase small bg-success text-white">Admin</div>
        <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Admin/Realms">...</a>
        <!-- ... more admin links ... -->
    }
}
```

## Behavior

### Now

- ✅ **Tenant Admins** see the Admin section
- ✅ **Platform Admins** see both Platform Admin and Admin sections
- ✅ **Regular Users** do NOT see the Admin section
- ✅ **Unauthenticated Users** do NOT see the Admin section

### Authorization Policies Used

- `tenant-admin`: Checks if user has the `tenant-admin` role in the current tenant's `default` realm
- `platform-admin`: Checks if user has the `platform-admin` role in the `platform` realm

## Implementation Details

The fix uses the same pattern already established for the Platform Admin section:

1. Check if user is authenticated
2. Call `IAuthorizationService.AuthorizeAsync()` with the relevant policy
3. Only render the section if authorization succeeds

This ensures consistency across the UI and follows the existing authorization architecture.

## Testing

After restarting the application:

1. **Log in as a regular user** (no admin roles)
   - Expected: "My Account" section visible, no "Admin" section
   
2. **Log in as a tenant admin**
   - Expected: "My Account" and "Admin" sections visible, no "Platform Admin" section
   
3. **Log in as a platform admin**
   - Expected: All sections visible ("My Account", "Platform Admin", and "Admin")

## Notes

- All Admin pages already have `[Authorize(Policy = "tenant-admin")]` attributes, so this is purely a UI enhancement
- Users who somehow navigate to admin pages without authorization will still get "Access Denied" (the authorization handlers enforce this)
- This change improves UX by hiding navigation items that users don't have permission to access

## Related Files

- Authorization handler: `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthorizationHandler.cs`
- Platform admin handler: `MrWhoOidc.WebAuth/Security/Admin/PlatformAdminAuthorizationHandler.cs`
- Policy configuration: `MrWhoOidc.WebAuth/Program.cs` (lines registering authorization policies)

## Date

2025-10-14
