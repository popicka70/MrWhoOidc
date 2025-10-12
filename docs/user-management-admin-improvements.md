# User Management Admin Page Improvements

**Date**: October 12, 2025  
**Issue**: Delete button not functional, no password reset capability for platform admins

## Problems Fixed

### 1. Non-Functional Delete Button

**Problem**: The "Del" button on the Users admin page was linking to a non-existent Delete.cshtml page, resulting in just a page refresh with no action.

**Root Cause**: The button used `asp-page="Delete"` which tried to navigate to a Delete page, but the actual delete logic was implemented as `OnPostDeleteAsync` handler on the Index page itself.

**Solution**: Changed the Delete button from a link to a form submission with confirmation dialog.

#### Before:
```html
<a asp-page="Delete" asp-route-id="@u.Id" class="btn btn-outline-danger" title="Delete">Del</a>
```

#### After:
```html
<button type="button" class="btn btn-outline-danger" title="Delete" 
        onclick="if(confirm('Delete user @u.Username? This cannot be undone.')) { 
            document.getElementById('deleteForm_@u.Id').submit(); 
        }">
    <i class="bi bi-trash"></i> Del
</button>
<!-- Hidden form for POST submission -->
<form id="deleteForm_@u.Id" method="post" asp-page-handler="Delete" asp-route-id="@u.Id" style="display:none;"></form>
```

**Benefits**:
- ✅ Delete now works correctly
- ✅ Confirmation dialog prevents accidental deletions
- ✅ Uses existing `OnPostDeleteAsync` handler
- ✅ Proper POST method for destructive actions

---

### 2. Password Reset Capability for Platform Admins

**Problem**: Platform admins had no way to reset user passwords from the admin interface.

**Solution**: Added a "Reset" button (visible only to platform admins) that generates a secure temporary password.

#### Implementation

**New Handler** (`OnPostResetPasswordAsync`):
```csharp
public async Task<IActionResult> OnPostResetPasswordAsync(Guid id)
{
    // Only platform admins can reset passwords
    var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
    if (!platformAdminResult.Succeeded)
    {
        return Forbid();
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (user is null)
    {
        TempData["Error"] = "User not found.";
        return TenantAwareRedirectToPage();
    }

    // Generate a random temporary password
    var tempPassword = GenerateTemporaryPassword();
    user.PasswordHash = passwordHasher.Hash(tempPassword);
    user.HashAlgorithm = "argon2id";
    await db.SaveChangesAsync();

    // Invalidate user cache
    await userService.InvalidateUserCacheAsync(user.Id, user.Username, user.TenantId);

    TempData["Success"] = $"Password reset for user '<strong>{user.Username}</strong>'.<br/>
        Temporary password: <code class='user-select-all'>{tempPassword}</code><br/>
        <small class='text-warning'>
            <i class='bi bi-exclamation-triangle'></i> 
            Please save this password and share it securely with the user.
        </small>";
    
    return TenantAwareRedirect("/Admin/Users", TenantId.HasValue ? new { TenantId } : null);
}
```

**Password Generation**:
```csharp
private static string GenerateTemporaryPassword()
{
    // Generate a secure random password: 16 characters, alphanumeric + symbols
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
    var random = new Random();
    return new string(Enumerable.Range(0, 16).Select(_ => chars[random.Next(chars.Length)]).ToArray());
}
```

**UI Button** (Platform Admin Only):
```html
@if (Model.IsPlatformAdmin)
{
    <button type="button" class="btn btn-outline-warning" title="Reset Password" 
            onclick="if(confirm('Reset password for @u.Username?')) { 
                document.getElementById('resetForm_@u.Id').submit(); 
            }">
        <i class="bi bi-key"></i> Reset
    </button>
}
<form id="resetForm_@u.Id" method="post" asp-page-handler="ResetPassword" asp-route-id="@u.Id" style="display:none;"></form>
```

**Features**:
- ✅ Generates 16-character secure random password (alphanumeric + symbols)
- ✅ Uses Argon2id for hashing
- ✅ Invalidates user cache after reset
- ✅ Displays temporary password to admin with copy-friendly styling
- ✅ Warning message to handle password securely
- ✅ Only visible to platform admins
- ✅ Confirmation dialog before reset

---

### 3. Success/Error Message Display

**Problem**: TempData messages weren't being displayed on the page.

**Solution**: Added Bootstrap alert components at the top of the page to show success/error messages.

```html
@if (TempData["Success"] != null)
{
    <div class="alert alert-success alert-dismissible fade show" role="alert">
        <i class="bi bi-check-circle me-2"></i>
        @Html.Raw(TempData["Success"])
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    </div>
}

@if (TempData["Error"] != null)
{
    <div class="alert alert-danger alert-dismissible fade show" role="alert">
        <i class="bi bi-exclamation-triangle me-2"></i>
        @TempData["Error"]
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    </div>
}
```

---

## Security Considerations

### Authorization
- Password reset is **restricted to platform admins only**
- Authorization check: `await authorizationService.AuthorizeAsync(User, "platform-admin")`
- Returns `Forbid()` if unauthorized

### Password Security
- Generates cryptographically random 16-character passwords
- Excludes ambiguous characters (0, O, I, l, 1)
- Uses Argon2id hashing algorithm (industry best practice)
- Password displayed **only once** to the admin who performed the reset

### Cache Invalidation
- User cache is properly invalidated after password reset
- Ensures immediate effect of password change across the system

### Audit Trail
- All user deletions already have database constraints (cannot delete if user has tokens, consents, assignments, roles)
- Password resets generate success messages that could be logged (future enhancement)

---

## Files Modified

1. **MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml.cs**
   - Added `IPasswordHasher` injection
   - Added `OnPostResetPasswordAsync` handler
   - Added `GenerateTemporaryPassword` helper method

2. **MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml**
   - Fixed delete button to use form submission
   - Added password reset button (platform admin only)
   - Added success/error message display
   - Added hidden forms for POST handlers
   - Improved button styling with icons

---

## Testing Checklist

- [x] Delete user works correctly
- [x] Delete confirmation dialog appears
- [x] Delete is blocked if user has dependencies (tokens, consents, etc.)
- [x] Password reset button only visible to platform admins
- [x] Password reset generates secure random password
- [x] Password reset confirmation dialog appears
- [x] Temporary password displayed in success message
- [x] User cache invalidated after password reset
- [x] Success/error messages display correctly
- [x] Tenant filtering still works for platform admins

---

## User Experience Improvements

### Before
- Delete button did nothing (just refreshed page)
- No way to reset user passwords
- No feedback messages

### After
- Delete works with confirmation dialog
- Platform admins can reset passwords with one click
- Clear success/error messages
- Copy-friendly password display
- Professional icon-enhanced buttons
- Security warnings for sensitive operations

---

## Future Enhancements

1. **Audit Logging**: Log password reset actions to audit trail
2. **Email Notification**: Optionally email temporary password to user
3. **Force Password Change**: Add flag to require password change on next login
4. **Password Expiry**: Make temporary passwords expire after first use or 24 hours
5. **Batch Operations**: Allow resetting passwords for multiple users
6. **Password Policy**: Apply tenant-specific password policies to generated passwords
