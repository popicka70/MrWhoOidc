# Identity Provider Admin API Tenant Filtering Fix

**Date**: October 11, 2025  
**Status**: ✅ Complete  
**Impact**: Security & Authorization

---

## Problem

The Identity Provider Admin API endpoints (`/admin/api/providers`) were using the legacy `"admin"` authorization policy instead of the tenant-aware `"tenant-admin"` policy. This caused:

1. **Access Denied** for tenant admins trying to manage identity providers through the UI
2. **No tenant filtering** - the API could have allowed cross-tenant access if accessed directly
3. **Missing tenant assignment** - new providers created via API didn't get assigned to a tenant
4. **Inconsistency** - Razor Pages used `"tenant-admin"` while APIs used `"admin"`

### Authorization Policies

| Policy | Description | Scope |
|--------|-------------|-------|
| `"admin"` | Legacy non-tenant-aware admin role | Single "admin" realm |
| `"tenant-admin"` | Tenant-specific admin role | Per-tenant "default" realm |
| `"platform-admin"` | Cross-tenant super admin | Global "platform" realm |

---

## Solution

Updated all Identity Provider Admin API endpoints in `AdminApiEndpointMappingExtensions.cs`:

### Changes Made

1. **Changed Authorization Policy**
   - From: `.RequireAuthorization("admin")`
   - To: `.RequireAuthorization("tenant-admin")`

2. **Added Tenant Filtering**
   - Platform admins can access all providers (existing behavior)
   - Tenant admins can only access providers in their tenant (new security layer)

3. **Added Tenant Assignment**
   - `POST /admin/api/providers` now automatically assigns `TenantId` from current context
   - Prevents providers from being created without tenant association

4. **Created Helper Method**
   - `ValidateProviderAccessAsync()` - centralized tenant validation logic
   - Used by all provider sub-resources (claim mappings, keys)

### Updated Endpoints

#### Provider CRUD
- ✅ `GET /admin/api/providers` - Lists providers with tenant filtering
- ✅ `GET /admin/api/providers/{id}` - Gets provider with tenant validation
- ✅ `POST /admin/api/providers` - Creates provider with tenant assignment
- ✅ `PUT /admin/api/providers/{id}` - Updates provider with tenant validation
- ✅ `DELETE /admin/api/providers/{id}` - Deletes provider with tenant validation

#### Provider Sub-Resources
- ✅ `GET /admin/api/providers/{id}/claim-mappings` - With tenant validation
- ✅ `POST /admin/api/providers/{id}/claim-mappings` - With tenant validation
- ✅ `PUT /admin/api/providers/{id}/claim-mappings/{mappingId}` - With tenant validation
- ✅ `DELETE /admin/api/providers/{id}/claim-mappings/{mappingId}` - With tenant validation
- ✅ `GET /admin/api/providers/{id}/keys` - With tenant validation
- ✅ `POST /admin/api/providers/{id}/keys` - With tenant validation
- ✅ `PUT /admin/api/providers/{id}/keys/{keyId}` - With tenant validation
- ✅ `DELETE /admin/api/providers/{id}/keys/{keyId}` - With tenant validation

---

## Security Benefits

### Before
```
Tenant Admin → Admin UI (Add Provider button)
                    ↓
              REST API Call
                    ↓
        ❌ Access Denied (needs "admin" role)
```

### After
```
Tenant Admin → Admin UI (Add Provider button)
                    ↓
              REST API Call
                    ↓
        ✅ Success (has "tenant-admin" role)
        ✅ Provider assigned to current tenant
        ✅ Cannot access other tenants' providers
```

### Platform Admin Behavior (Unchanged)
```
Platform Admin → Admin UI
                    ↓
        ✅ Can view/edit ALL providers (all tenants)
        ✅ Full cross-tenant access preserved
```

---

## Code Example: Tenant Filtering Logic

```csharp
// Platform admins can access all providers
var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
if (platformAdminResult.Succeeded)
{
    // No filtering needed
}
else
{
    // Tenant admins: filter by their tenant
    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
    {
        return Results.Problem(statusCode: 403, title: "No tenant context");
    }
    query = query.Where(p => p.TenantId == currentTenantId.Value);
}
```

---

## Testing Checklist

- [ ] Tenant admin can create identity provider via UI
- [ ] Tenant admin can view only their tenant's providers
- [ ] Tenant admin cannot access other tenants' providers via API
- [ ] Platform admin can view/edit all providers across tenants
- [ ] Provider claim mappings respect tenant boundaries
- [ ] Provider keys respect tenant boundaries
- [ ] New providers are assigned to correct tenant
- [ ] Existing tests still pass

---

## Related Files

- `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs` - **UPDATED**
- `MrWhoOidc.WebAuth/Pages/Admin/Providers/*.cshtml.cs` - Already tenant-aware (reference)
- `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthorizationHandler.cs` - Authorization logic
- `MrWhoOidc.WebAuth/Security/Admin/AdminAuthorizationHandler.cs` - Legacy admin (deprecated for tenant-specific resources)

---

## Migration Notes

### Breaking Changes
- **None for users** - This is a bug fix that restores expected behavior
- **None for APIs** - Policy change is internal, API signatures unchanged

### Deprecated
- Using `"admin"` policy for tenant-specific resources is now anti-pattern
- All new tenant-scoped admin endpoints should use `"tenant-admin"` policy

### Future Work
- [ ] Consider migrating other admin endpoints to tenant-admin where appropriate
- [ ] Add audit logging for cross-tenant access by platform admins
- [ ] Document policy selection guidelines in developer guide

---

## References

- **Authorization Handler**: `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthorizationHandler.cs`
- **Policy Definition**: `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/AuthenticationAuthorizationExtensions.cs`
- **Tenant Context**: `MrWhoOidc.Auth/MultiTenancy/ITenantAccessor.cs`
- **Instructions**: `.github/copilot-instructions.md` (architecture rules)
