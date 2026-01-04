# Feature 4: Read-Only Impersonation - Testing Guide

**Date:** October 6, 2025  
**Feature:** Read-Only Mode During Impersonation  
**Status:** Ready for Testing

## Overview

Feature 4 prevents platform administrators from making changes while impersonating tenants. This is enforced automatically through the `ReadOnlyAdminPageModel` base class, which blocks all POST requests during impersonation.

## Test Environment Setup

✅ **Application Started:** `dotnet run --project MrWhoOidc.AppHost`  
✅ **Aspire Dashboard:** https://localhost:17140/login?t=4ac65f853e7a25432b2d7134b454e18b

### Service URLs (from Aspire Dashboard)

Check the Aspire dashboard for the actual URLs. Typically:
- **WebAuth (MrWhoOidc.WebAuth):** https://localhost:7xxx
- **Web Frontend:** https://localhost:7xxx
- **API Service:** https://localhost:7xxx

## Test Scenarios

### ✅ Test 1: Visual Banner Display

**Objective:** Verify the red danger banner appears during impersonation

**Steps:**
1. Navigate to WebAuth URL (e.g., https://localhost:7xxx)
2. Login as platform admin:
   - Username: `admin@platform.local` (or check seed data)
   - Password: (check seed data in `TestDataSeeder.cs`)
3. Navigate to `/PlatformAdmin/Impersonation`
4. Select a tenant and click "Start Impersonation"

**Expected Results:**
- ✅ Red danger banner appears at top of page
- ✅ Banner shows: "🔒 READ-ONLY IMPERSONATION MODE"
- ✅ Banner emphasizes "All write operations are disabled"
- ✅ Large red "Exit Read-Only Mode" button visible
- ✅ Banner uses `alert-danger` styling (red, not yellow)

**Screenshot Location:** (Take screenshots and save in `/docs/screenshots/`)

---

### ✅ Test 2: POST Blocking - User Edit

**Objective:** Verify POST requests are blocked when editing users

**Steps:**
1. While impersonating (banner visible), navigate to `/Admin/Users`
2. Click "Edit" on any user
3. Modify any field (e.g., email, display name)
4. Click "Save" button

**Expected Results:**
- ✅ Form submission is blocked (HTTP 403 Forbidden)
- ✅ Error message appears: "⚠️ Cannot perform this action in read-only impersonation mode. Exit impersonation to make changes."
- ✅ User data remains unchanged
- ✅ Page stays on edit form (no redirect to index)

**Actual Results:**
- Status Code: ___________
- Error Message: ___________
- Data Changed: Yes / No

---

### ✅ Test 3: POST Blocking - Client Edit

**Objective:** Verify POST requests are blocked when editing clients

**Steps:**
1. While impersonating, navigate to `/Admin/Clients`
2. Click "Edit" on any client
3. Modify any field (e.g., client name, redirect URIs)
4. Click "Save" button

**Expected Results:**
- ✅ Form submission is blocked (HTTP 403 Forbidden)
- ✅ Error message appears in TempData
- ✅ Client data remains unchanged

**Actual Results:**
- Status Code: ___________
- Error Message: ___________
- Data Changed: Yes / No

---

### ✅ Test 4: POST Blocking - Multiple Handlers (Clients)

**Objective:** Verify all POST handlers in Clients/Edit are blocked

**Note:** Clients/Edit has 12 POST handlers - test a few key ones:

**Test 4a: Add Scope**
1. Navigate to `/Admin/Clients/{id}/Edit` (Scopes tab)
2. Enter a scope name and click "Add Scope"

**Expected:** ✅ Blocked, error message shown

**Test 4b: Generate Key**
1. Navigate to Keys tab
2. Click "Generate New Key Pair"

**Expected:** ✅ Blocked, error message shown

**Test 4c: Remove Redirect URI**
1. Navigate to Basic Settings
2. Try to remove a redirect URI

**Expected:** ✅ Blocked, error message shown

**Actual Results:**
- Add Scope: Blocked Yes/No, Error: ___________
- Generate Key: Blocked Yes/No, Error: ___________
- Remove URI: Blocked Yes/No, Error: ___________

---

### ✅ Test 5: POST Blocking - Other Admin Pages

**Objective:** Verify enforcement across all admin pages

**Test 5a: Scopes Edit**
1. Navigate to `/Admin/Scopes`
2. Edit any scope
3. Try to save

**Expected:** ✅ Blocked

**Test 5b: Roles Edit**
1. Navigate to `/Admin/Roles`
2. Edit any role
3. Try to save

**Expected:** ✅ Blocked

**Test 5c: Realms Edit**
1. Navigate to `/Admin/Realms`
2. Edit any realm
3. Try to save

**Expected:** ✅ Blocked

**Test 5d: Providers Edit**
1. Navigate to `/Admin/Providers`
2. Edit any provider
3. Try to save

**Expected:** ✅ Blocked

**Test 5e: Provider Delete**
1. Navigate to `/Admin/Providers`
2. Click "Delete" on any provider
3. Try to confirm deletion

**Expected:** ✅ Blocked

**Actual Results:**
- Scopes: Blocked Yes/No ___________
- Roles: Blocked Yes/No ___________
- Realms: Blocked Yes/No ___________
- Providers Edit: Blocked Yes/No ___________
- Providers Delete: Blocked Yes/No ___________

---

### ✅ Test 6: Normal Operation After Exit

**Objective:** Verify POST requests work normally after exiting impersonation

**Steps:**
1. While impersonating, click "Exit Read-Only Mode" button in banner
2. Verify banner disappears
3. Navigate to `/Admin/Users`
4. Edit any user
5. Modify a field and click "Save"

**Expected Results:**
- ✅ Banner is hidden
- ✅ Form submission succeeds (HTTP 200/302)
- ✅ Success message appears
- ✅ User data is updated
- ✅ Redirected to user list

**Actual Results:**
- Banner Hidden: Yes / No
- Status Code: ___________
- Success Message: ___________
- Data Changed: Yes / No

---

### ✅ Test 7: GET Requests Still Work

**Objective:** Verify read operations work during impersonation

**Steps:**
1. While impersonating, navigate through admin pages:
   - `/Admin/Users`
   - `/Admin/Clients`
   - `/Admin/Scopes`
   - `/Admin/Roles`
2. Click "Edit" to view edit forms (but don't submit)
3. View details pages

**Expected Results:**
- ✅ All pages load successfully
- ✅ Data is displayed correctly
- ✅ Edit forms are accessible (but submission blocked)
- ✅ No errors in browser console

**Actual Results:**
- Pages Load: Yes / No
- Edit Forms Visible: Yes / No
- Console Errors: Yes / No (describe if any)

---

### ✅ Test 8: Base Class Inheritance

**Objective:** Verify UserPageModelBase provides automatic enforcement

**Steps:**
1. Check that user admin pages inherit from `UserPageModelBase` → `ReadOnlyAdminPageModel`
2. Test user edit/create operations while impersonating

**Expected Results:**
- ✅ User pages automatically have read-only enforcement
- ✅ No manual checks needed in POST handlers

**Code Verification:**
```csharp
// UserPageModelBase.cs
public abstract class UserPageModelBase : ReadOnlyAdminPageModel

// Users/Edit.cshtml.cs (inherits via base)
public class EditModel(...) : UserPageModelBase
```

---

## Implementation Details Verified

### ✅ Base Class Filter (ReadOnlyAdminPageModel.cs)

```csharp
public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
{
    ImpersonationService = context.HttpContext.RequestServices
        .GetService(typeof(IImpersonationService)) as IImpersonationService;

    if (ImpersonationService != null)
    {
        IsReadOnlyMode = ImpersonationService.IsImpersonating(context.HttpContext);

        // Block all POST requests during impersonation
        if (IsReadOnlyMode && context.HttpContext.Request.Method == "POST")
        {
            TempData["Error"] = "⚠️ Cannot perform this action...";
            context.Result = new ForbidResult();
            return;
        }
    }
    base.OnPageHandlerExecuting(context);
}
```

**Key Points:**
- ✅ Runs before any POST handler
- ✅ Automatically detects impersonation via session
- ✅ Sets `IsReadOnlyMode` property for UI
- ✅ Returns `ForbidResult` (403) for POST requests
- ✅ Sets error message in TempData

### ✅ Pages Using Base Class

All these pages inherit from `ReadOnlyAdminPageModel`:
- `Admin/Users/Edit.cshtml.cs` (via UserPageModelBase)
- `Admin/Clients/Edit.cshtml.cs`
- `Admin/Scopes/Edit.cshtml.cs`
- `Admin/Roles/Edit.cshtml.cs`
- `Admin/Realms/Edit.cshtml.cs`
- `Admin/Providers/Edit.cshtml.cs`
- `Admin/Providers/Delete.cshtml.cs`
- `Admin/ProviderClaimMappings/Edit.cshtml.cs`

### ✅ Visual Banner (_ImpersonationBanner.cshtml)

```razor
@if (ImpersonationService?.IsImpersonating(Context) == true)
{
    <div class="alert alert-danger border-start border-5 border-danger">
        <h5>🔒 READ-ONLY IMPERSONATION MODE</h5>
        <p>All write operations are disabled.</p>
        <button class="btn btn-danger btn-lg shadow">Exit Read-Only Mode</button>
    </div>
}
```

---

## Test Results Summary

| Test | Status | Notes |
|------|--------|-------|
| 1. Banner Display | ⏳ Pending | |
| 2. User Edit Block | ⏳ Pending | |
| 3. Client Edit Block | ⏳ Pending | |
| 4. Multiple Handlers | ⏳ Pending | |
| 5. Other Admin Pages | ⏳ Pending | |
| 6. Normal After Exit | ⏳ Pending | |
| 7. GET Requests | ⏳ Pending | |
| 8. Base Class Inheritance | ✅ Code Verified | |

---

## Known Limitations

1. **Manual pages not covered:** Any admin pages that don't inherit from `ReadOnlyAdminPageModel` won't have automatic enforcement
2. **API endpoints:** If there are admin API endpoints (non-Razor), they need separate enforcement
3. **JavaScript submissions:** AJAX/fetch requests that don't use forms may need additional handling

---

## Next Steps After Testing

1. ✅ Fill in "Actual Results" for each test
2. ✅ Add screenshots to `/docs/screenshots/phase5b-feature4/`
3. ✅ Document any issues found
4. ✅ Update `phase5b-implementation-plan.md` to mark Feature 4 complete
5. ✅ Create user documentation for platform admins
6. ✅ Move to Feature 5: Audit Logging

---

## Test Execution Log

**Tester:** _________________  
**Date:** October 6, 2025  
**Environment:** Local Development (Aspire)  
**Browser:** _________________  
**Build Status:** ✅ Successful (no errors)

**Overall Result:** ⏳ In Progress

**Issues Found:**
- (List any issues here)

**Notes:**
- (Additional observations)
