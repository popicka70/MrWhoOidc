# Test Fixes: Phase 5A Snapshot Update

**Date**: January 2025  
**Status**: ✅ COMPLETE  
**Impact**: All 331 tests passing

## Summary
Updated the endpoint manifest snapshot test to approve the 11 new endpoints added during Phase 5A implementation (Tenant Switcher, Impersonation, and Mobile Responsive features).

## Problem
- **Test**: `ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable`
- **Status**: 1 of 331 tests failing
- **Cause**: Phase 5A legitimately added 11 new endpoints to the application surface
- **Snapshot file**: `MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json`
- **Additional issue**: Snapshot file was corrupted (had legacy "Added"/"Removed"/"Changed" wrapper structure with trailing junk)

## Solution
Regenerated the snapshot file to:
1. Approve the 11 new Phase 5A endpoints
2. Fix the corrupted snapshot structure (converted from wrapped object to clean array)

### Steps Taken
```powershell
# 1. Remove corrupted snapshot file
Remove-Item "MrWhoOidc.UnitTests\Snapshots\endpoint-manifest.snapshot.json" -Force

# 2. Run test to regenerate snapshot
dotnet test --filter "FullyQualifiedName=MrWhoOidc.UnitTests.ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable" --no-build
# Result: Test skipped (Inconclusive) - snapshot regenerated

# 3. Run test again to verify
dotnet test --filter "FullyQualifiedName=MrWhoOidc.UnitTests.ProgramSurfaceSnapshotTests.Endpoint_Manifest_Snapshot_Is_Stable" --no-build
# Result: Test passed ✅

# 4. Run all tests to confirm
dotnet test --no-build
# Result: 331/331 tests passing ✅
```

## New Endpoints Approved (11 total)

### Account Management Pages (7)
- `Account` - Account hub page
- `Account/Consents` - Manage OAuth consents
- `Account/Emails` - Manage email addresses  
- `Account/Index` - Account index page
- `Account/LinkedAccounts` - External identity links
- `Account/Profile` - User profile management
- `Account/Sessions` - Active session management

### MFA (1)
- `Mfa/Index` - Multi-factor authentication page

### Platform Admin Impersonation (2)
- `StartImpersonation` - Begin impersonating a tenant (platform-admin only)
- `StopImpersonation` - Stop impersonating (platform-admin only)

### Tenant Management (1)
- `SwitchTenant` - Switch between user's tenants

## Snapshot Structure Fix
**Before** (corrupted):
```json
{
  "Added": [ /* 100+ endpoints */ ],
  "Removed": [],
  "Changed": [],
  "DuplicateGroups": [],
  "IgnoredPresent": 0
}<trailing 89 chars of junk>
```

**After** (correct):
```json
[
  {
    "Pattern": "",
    "Methods": "",
    "RateLimiters": [],
    "Authz": null,
    "HasAntiforgery": true,
    "HasCors": false,
    "IsAnonymous": false
  },
  /* ...111 more endpoints... */
]
```

## Verification
✅ All 331 tests passing  
✅ No build errors  
✅ Snapshot file structure corrected  
✅ Phase 5A endpoints officially approved  

## Test Execution Time
- Single snapshot test: ~7 seconds
- Full test suite: ~12 seconds

## Next Steps
With tests fixed and baseline clean:
- ✅ Ready to proceed with Phase 5B implementation
- ✅ Snapshot will now catch any unintended API surface changes
- ✅ Endpoint manifest properly tracks all Razor Pages and minimal API endpoints

## Notes
- This is the expected workflow for snapshot tests when intentionally adding new endpoints
- The test detected a legitimate change (new endpoints from Phase 5A)
- Regenerating the snapshot "approves" the change for future comparisons
- Future unintended endpoint additions/removals will now be caught
