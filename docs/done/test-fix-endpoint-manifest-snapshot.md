# Test Fix - Endpoint Manifest Snapshot Update

**Date**: October 10, 2025  
**Test Fixed**: `Endpoint_Manifest_Snapshot_Is_Stable`  
**Status**: ✅ Fixed - All 366 tests passing

---

## Problem

The `Endpoint_Manifest_Snapshot_Is_Stable` test was failing after we changed the authorization policy for Scopes admin pages from `tenant-admin` to `platform-admin`.

### Test Failure:
```
Assert.AreEqual selhalo. Očekávaná délka řetězce je 30457, ale byla 30461.
Endpoint manifest changed. If intentional, update the snapshot file to approve new surface.

Changed (2):
  * Admin/Scopes/Add|
      Old: Admin/Scopes/Add [] authz=tenant-admin anti=Y cors=N anon=N limiters=-
      New: Admin/Scopes/Add [] authz=platform-admin anti=Y cors=N anon=N limiters=-
  * Admin/Scopes/Edit/{name}|
      Old: Admin/Scopes/Edit/{name} [] authz=tenant-admin anti=Y cors=N anon=N limiters=-
      New: Admin/Scopes/Edit/{name} [] authz=platform-admin anti=Y cors=N anon=N limiters=-
```

---

## Root Cause

This test is a **snapshot test** that verifies the API surface remains stable by comparing the current endpoint configuration against a saved snapshot file.

When we changed the Scopes pages to require `platform-admin` instead of `tenant-admin`, the endpoint manifest changed, causing the test to fail.

---

## Solution

Updated the endpoint manifest snapshot file to reflect the intentional authorization policy change:

**File Modified**: `MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json`

### Changes Made:

1. **Admin/Scopes/Add**:
   - Changed `"Authz": "tenant-admin"` → `"Authz": "platform-admin"`

2. **Admin/Scopes/Edit/{name}**:
   - Changed `"Authz": "tenant-admin"` → `"Authz": "platform-admin"`

### Note:
We did **NOT** change `Admin/Scopes/Index` because:
- The Index page still allows `tenant-admin` (view access)
- Only the DELETE handler requires `platform-admin` (enforced in code, not at page level)

---

## Verification

### Test Results:
```
✅ Total: 366 tests
✅ Failed: 0
✅ Passed: 366
✅ Skipped: 0
✅ Duration: ~10-12 seconds
```

### Build Status:
```
✅ MrWhoOidc.Security: Success
✅ MrWhoOidc.Auth: Success
✅ MrWhoOidc.ServiceDefaults: Success
✅ MrWhoOidc.Client: Success
✅ MrWhoOidc.WebAuth: Success
✅ MrWhoOidc.UnitTests: Success
```

---

## Why This Test Exists

The `Endpoint_Manifest_Snapshot_Is_Stable` test is a **regression test** that:

1. **Detects unintentional API changes** - If someone accidentally changes authorization, rate limiting, or CORS settings
2. **Documents the API surface** - The snapshot file serves as documentation of all endpoints
3. **Enforces explicit approval** - Changes must be intentional and snapshot updated

This is similar to snapshot testing in frontend frameworks (like Jest) but for backend API surfaces.

---

## When to Update This Snapshot

Update the snapshot file when you **intentionally** change:

- Authorization policies (`[Authorize(Policy = "...")]`)
- Rate limiting policies
- CORS settings
- Antiforgery requirements
- Anonymous access settings
- Endpoint patterns or HTTP methods

### Steps to Update:

1. Make your code changes
2. Run tests: `dotnet test`
3. Review the diff in `endpoint-manifest.diff.json`
4. If changes are intentional, update `endpoint-manifest.snapshot.json`
5. Re-run tests to verify

---

## Related Changes

This test fix complements the security changes made earlier:

- ✅ Scopes/Add.cshtml.cs - Changed to `platform-admin`
- ✅ Scopes/Edit.cshtml.cs - Changed to `platform-admin`
- ✅ Scopes/Index.cshtml.cs - DELETE handler checks `platform-admin` in code
- ✅ Snapshot file updated to match

---

## Files Modified

1. ✅ `MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json`
   - Updated `Admin/Scopes/Add` authorization
   - Updated `Admin/Scopes/Edit/{name}` authorization

---

## Continuous Integration Note

This fix ensures that:
- CI/CD pipelines will pass
- Pull request checks will succeed
- No regressions in API surface
- Security policy changes are explicitly tracked

---

**Status**: ✅ Complete  
**All Tests**: ✅ Passing (366/366)  
**Ready For**: Commit & Push
