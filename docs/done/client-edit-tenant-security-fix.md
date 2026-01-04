# Client Edit Page Tenant Security Fix

**Date**: October 10, 2025  
**Issue**: The client edit page save button (and other handlers) were not tenant-aware, allowing potential cross-tenant access vulnerabilities.

## Problem Description

The `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` page had several security issues:

1. **OnGetAsync**: Loaded clients by ID only, without filtering by tenant. This meant a tenant admin from Tenant A could view/edit clients from Tenant B if they knew the GUID.

2. **OnPostSaveAsync**: 
   - Loaded the client without tenant filtering
   - Checked ClientId uniqueness globally instead of per-tenant

3. **All POST handlers**: No tenant validation was performed before allowing modifications to client data.

### Security Impact

A malicious tenant admin could:
- Access and modify clients from other tenants by guessing or discovering their GUIDs
- Create clients with duplicate ClientIds across tenants (though this should be allowed per tenant)
- Modify JWKS, scopes, providers, and other sensitive client configuration for clients outside their tenant

## Solution

### 1. Injected Required Services

Added `ITenantAccessor` and `IAuthorizationService` to the constructor:

```csharp
public class EditModel(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    ILogger<EditModel> logger, 
    MrWhoOidc.WebAuth.Observability.IAuditSink audit, 
    OidcOptions oidcOptions,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ReadOnlyAdminPageModel
```

### 2. Created Validation Helper Method

Added `ValidateTenantAccessAsync()` helper method:

```csharp
/// <summary>
/// Validates that the current user has access to the client based on tenant filtering.
/// Returns true if access is allowed (platform admin or client belongs to user's tenant).
/// </summary>
private async Task<bool> ValidateTenantAccessAsync()
{
    var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
    if (platformAdminResult.Succeeded)
    {
        return true; // Platform admins can access all clients
    }

    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
    {
        return false; // No tenant context
    }

    // Check if client belongs to the current tenant
    return await db.Clients.AnyAsync(c => c.Id == Id && c.TenantId == currentTenantId.Value);
}
```

### 3. Updated OnGetAsync

Added tenant filtering when loading the client:

```csharp
public async Task<IActionResult> OnGetAsync()
{
    // Check platform admin status
    var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
    var isPlatformAdmin = platformAdminResult.Succeeded;

    // Build query with tenant filtering
    var clientQuery = db.Clients.AsNoTracking().Where(c => c.Id == Id);
    
    if (!isPlatformAdmin)
    {
        // Regular tenant admins: filter by current tenant
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound(); // No tenant context
        }
        clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
    }

    var client = await clientQuery.FirstOrDefaultAsync();
    if (client is null) return NotFound();
    // ... rest of method
}
```

### 4. Updated OnPostSaveAsync

Added tenant filtering when loading the client and checking uniqueness:

```csharp
// Check platform admin status
var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
var isPlatformAdmin = platformAdminResult.Succeeded;

// Build query with tenant filtering
var clientQuery = db.Clients.Where(c => c.Id == Id);

if (!isPlatformAdmin)
{
    // Regular tenant admins: filter by current tenant
    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
    {
        return NotFound(); // No tenant context
    }
    clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
}

var client = await clientQuery.FirstOrDefaultAsync();
if (client is null) return NotFound();

// ... later in the method ...

// If client id changed, enforce uniqueness within tenant
if (!string.Equals(client.ClientId, Input.ClientId, StringComparison.Ordinal))
{
    var exists = await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId && c.TenantId == client.TenantId);
    if (exists)
    {
        // ... handle error
    }
}
```

### 5. Updated All POST Handlers

Added tenant validation to all POST handlers that modify client data:

- `OnPostAddScopeAsync`
- `OnPostRemoveScopeAsync`
- `OnPostExtractPublicJwkAsync`
- `OnPostGenerateJwksAsync`
- `OnPostAddKeyAsync`
- `OnPostFetchJwksAsync`
- `OnPostValidateJwtAsync`
- `OnPostSignTestJwtAsync`
- `OnPostRemoveKeyAsync`
- `OnPostAddProviderAsync`
- `OnPostDeleteProviderAsync`

Each handler now starts with:

```csharp
if (!await ValidateTenantAccessAsync())
{
    return NotFound();
}
```

## Testing Recommendations

1. **Cross-tenant access test**: As a tenant admin in Tenant A, try to access `/t/{tenant-a-slug}/Admin/Clients/Edit/{client-id-from-tenant-b}`. Should return 404.

2. **Platform admin access**: As a platform admin, verify you can still access and edit clients from any tenant.

3. **ClientId uniqueness**: Verify that two different tenants can now have clients with the same ClientId (tenant-scoped uniqueness).

4. **POST handler security**: Try to POST to any of the handlers (e.g., add scope, generate keys) for a client in another tenant. Should return 404.

## Files Modified

- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`

## Related Security Pattern

This fix follows the same pattern used in other admin pages:
- `MrWhoOidc.WebAuth/Pages/Admin/Realms/Edit.cshtml.cs`
- `MrWhoOidc.WebAuth/Pages/Admin/Roles/Edit.cshtml.cs`
- `MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml.cs`

## Migration Notes

- **Breaking change**: ClientId uniqueness is now scoped per-tenant instead of global. This is the correct behavior for multi-tenant systems.
- No database migration required.
- Existing data is not affected, but behavior changes for new client creation.

## Security Classification

**Severity**: High  
**Type**: Authorization bypass / Insecure Direct Object Reference (IDOR)  
**Status**: Fixed
