# Test Fix: Identity Provider Admin API Authorization Policy Change

**Date**: October 11, 2025  
**Status**: ✅ COMPLETE  
**Impact**: Snapshot test updated to approve authorization policy changes

---

## Summary

Updated the endpoint manifest snapshot test to approve the authorization policy change from `"admin"` to `"tenant-admin"` for Identity Provider Admin API endpoints.

---

## Problem

- **Test**: `ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable`
- **Status**: 1 of 366 tests failing
- **Cause**: We intentionally changed authorization policy for provider-related endpoints
- **Snapshot file**: `MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json`

### Test Failure Details

```
Assert.AreEqual selhalo. Očekávaná délka řetězce je 30461, ale byla 30615.
Endpoint manifest changed. If intentional, update the snapshot file to approve new surface.
Očekáváno: "...],␍␊    "Authz": "admin",␍␊    "Has..."
Ale bylo:  "...],␍␊    "Authz": "tenant-admin",␍␊ ..."
```

The test detected that 22 endpoints changed their authorization policy from `"admin"` to `"tenant-admin"`.

---

## Changed Endpoints (22 total)

### Identity Provider Management (13)
- `GET /admin/api/providers`
- `GET /admin/api/providers/{id:guid}`
- `POST /admin/api/providers`
- `PUT /admin/api/providers/{id:guid}`
- `DELETE /admin/api/providers/{id:guid}`
- `GET /admin/api/providers/{providerId:guid}/claim-mappings`
- `POST /admin/api/providers/{providerId:guid}/claim-mappings`
- `PUT /admin/api/providers/{providerId:guid}/claim-mappings/{id:guid}`
- `DELETE /admin/api/providers/{providerId:guid}/claim-mappings/{id:guid}`
- `GET /admin/api/providers/{providerId:guid}/keys`
- `POST /admin/api/providers/{providerId:guid}/keys`
- `PUT /admin/api/providers/{providerId:guid}/keys/{id:guid}`
- `DELETE /admin/api/providers/{providerId:guid}/keys/{id:guid}`

### Client-Provider Mappings (4)
- `GET /admin/api/clients/{clientId:guid}/providers`
- `POST /admin/api/clients/{clientId:guid}/providers`
- `PUT /admin/api/clients/{clientId:guid}/providers/{identityProviderId:guid}`
- `DELETE /admin/api/clients/{clientId:guid}/providers/{identityProviderId:guid}`

### Client Keys (2)
- `GET /admin/api/clients/{clientId:guid}/keys`
- `PUT /admin/api/clients/{clientId:guid}/keys`

### Back-Channel Logout Admin (3)
- `GET /admin/api/bcl/alerts/snapshot`
- `GET /admin/api/bcl/outbox`
- `POST /admin/api/bcl/outbox/{id:guid}/retry`

---

## Solution

### Steps Taken

```powershell
# 1. Remove old snapshot file
Remove-Item "MrWhoOidc.UnitTests\Snapshots\endpoint-manifest.snapshot.json" -Force

# 2. Run test to regenerate snapshot
dotnet test --filter "FullyQualifiedName=MrWhoOidc.UnitTests.ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable" --no-build
# Result: Test skipped (Inconclusive) - snapshot regenerated

# 3. Run test again to verify
dotnet test --filter "FullyQualifiedName=MrWhoOidc.UnitTests.ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable" --no-build
# Result: Test passed ✅

# 4. Run all tests to confirm
dotnet test --no-build
# Result: 366/366 tests passing ✅
```

---

## Test Results

### Before
```
Souhrn testu: celkem: 366; selhalo: 1; úspěšné: 365; přeskočeno: 0
```

### After
```
Souhrn testu: celkem: 366; selhalo: 0; úspěšné: 366; přeskočeno: 0
```

✅ **All 366 tests passing**

---

## What This Test Does

The `Endpoint_Manifest_Snapshot_Is_Stable` test is a **regression test** that:

1. **Enumerates all HTTP endpoints** in the application
2. **Captures metadata** for each endpoint:
   - Route pattern
   - HTTP methods
   - Authorization policy
   - Rate limiting policies
   - CORS settings
   - Antiforgery requirements
   - Anonymous access
3. **Compares against a snapshot** file
4. **Fails if the surface changes** unexpectedly

This protects against:
- Accidentally exposing new endpoints
- Accidentally changing security policies
- Unintended API surface modifications

---

## Why This Change Is Intentional

We changed the authorization policy from `"admin"` to `"tenant-admin"` because:

1. **Tenant Isolation**: Identity providers should be tenant-scoped
2. **Security**: Tenant admins should only manage their own providers
3. **Consistency**: Aligns with other tenant-scoped resources (users, clients, realms)
4. **Bug Fix**: Restores expected behavior - tenant admins can now manage providers

The snapshot test correctly detected this change and required explicit approval by regenerating the snapshot file.

---

## Related Documentation

- `docs/identity-provider-admin-api-tenant-filtering-fix.md` - Main feature documentation
- `docs/identity-provider-tenant-filtering-quickref.md` - Quick reference
- `docs/test-fixes-phase5a-snapshot-update.md` - Previous snapshot update example

---

## Verification Checklist

- [x] Snapshot test passes
- [x] All 366 tests pass
- [x] Snapshot file regenerated with new authorization policies
- [x] Changes are intentional and documented
- [x] Security implications reviewed and approved
