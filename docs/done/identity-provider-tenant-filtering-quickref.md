# Identity Provider Tenant Filtering - Quick Reference

## Problem Fixed
Tenant admins couldn't add identity providers - got "Access Denied" error.

## Root Cause
Admin API used wrong authorization policy:
- ❌ Was: `.RequireAuthorization("admin")` (legacy, non-tenant-aware)
- ✅ Now: `.RequireAuthorization("tenant-admin")` (tenant-aware)

## What Changed

### File: `AdminApiEndpointMappingExtensions.cs`

1. **Policy**: Changed from `"admin"` to `"tenant-admin"`
2. **Tenant Filtering**: Added for all provider CRUD operations
3. **Tenant Assignment**: New providers auto-assigned to current tenant
4. **Sub-resources**: Claim mappings and keys now validate provider tenant access

## Affected Endpoints (13 total)

| Endpoint | What Changed |
|----------|--------------|
| `GET /admin/api/providers` | ✅ Tenant filtering |
| `GET /admin/api/providers/{id}` | ✅ Tenant validation |
| `POST /admin/api/providers` | ✅ Tenant assignment |
| `PUT /admin/api/providers/{id}` | ✅ Tenant validation |
| `DELETE /admin/api/providers/{id}` | ✅ Tenant validation |
| `GET /admin/api/providers/{id}/claim-mappings` | ✅ Tenant validation |
| `POST /admin/api/providers/{id}/claim-mappings` | ✅ Tenant validation |
| `PUT /admin/api/providers/{id}/claim-mappings/{mappingId}` | ✅ Tenant validation |
| `DELETE /admin/api/providers/{id}/claim-mappings/{mappingId}` | ✅ Tenant validation |
| `GET /admin/api/providers/{id}/keys` | ✅ Tenant validation |
| `POST /admin/api/providers/{id}/keys` | ✅ Tenant validation |
| `PUT /admin/api/providers/{id}/keys/{keyId}` | ✅ Tenant validation |
| `DELETE /admin/api/providers/{id}/keys/{keyId}` | ✅ Tenant validation |

## How It Works Now

### Tenant Admin
```
1. Logs into tenant (e.g., /t/acme-corp)
2. Has "tenant-admin" role in that tenant
3. Clicks "Add Provider"
4. ✅ Can create/edit providers in THEIR tenant only
5. ❌ Cannot see/edit other tenants' providers
```

### Platform Admin
```
1. Has "platform-admin" role globally
2. Can access any tenant's admin UI
3. ✅ Can view/edit ALL providers across ALL tenants
4. ✅ No restrictions applied
```

## Quick Test

1. **Login as tenant admin** to `/t/your-tenant/Admin/Providers`
2. **Click "Add Provider"** button
3. **Should work now** (previously failed with Access Denied)
4. **Check the provider** - should have `TenantId` set to your tenant

## Build Status
✅ Solution builds successfully  
✅ No breaking changes  
✅ Backward compatible (platform admins unaffected)

## Next Steps
- [ ] Test as tenant admin
- [ ] Verify tenant isolation
- [ ] Test as platform admin (should still see all)
- [ ] Run integration tests
