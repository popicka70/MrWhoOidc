# Scopes: Global Resource Design & Tenant Separation

## Date: October 11, 2025

## Design Overview

**Scopes are GLOBAL/SHARED resources** in MrWhoOidc - they do NOT have a `TenantId` field. This is an intentional architectural decision.

### Why Global Scopes?

1. **OAuth2/OIDC Standard Scopes:** Standard scopes like `openid`, `profile`, `email`, `offline_access` should be consistent across all tenants
2. **Simplified Client Configuration:** Clients across tenants can reference the same scope names
3. **API Resource Consistency:** APIs can validate the same scope names regardless of which tenant issued the token
4. **Reduced Data Duplication:** No need to create identical scopes in every tenant

### Database Schema

```csharp
public class Scope
{
    [Key]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    [MaxLength(200)]
    public string? Description { get; set; }
    
    public bool IsExposed { get; set; } = true;
    
    // NOTE: No TenantId field - scopes are global
}
```

## Authorization Model

### Viewing Scopes
- **Who:** Both `platform-admin` AND `tenant-admin` can view scopes
- **Why:** Tenant admins need to see available scopes when configuring clients
- **Policy:** `tenant-admin` (allows both platform and tenant admins)

### Managing Scopes (Add/Edit/Delete)
- **Who:** ONLY `platform-admin` can manage scopes
- **Why:** Prevents tenant admins from:
  - Polluting the shared scope catalog
  - Modifying standard scopes like `openid`, `profile`
  - Creating conflicting scope names
  - Deleting scopes used by other tenants' clients
- **Policy:** `platform-admin` (enforced on Add/Edit pages)

## Implementation Details

### Page Authorization Policies

```csharp
// Index.cshtml.cs - VIEW scopes
[Authorize(Policy = "tenant-admin")]  // Both platform and tenant admins
public class IndexModel { }

// Add.cshtml.cs - CREATE scopes
[Authorize(Policy = "platform-admin")]  // Platform admins only
public class AddModel { }

// Edit.cshtml.cs - MODIFY scopes
[Authorize(Policy = "platform-admin")]  // Platform admins only
public class EditModel { }
```

### UI Conditional Rendering

The Index page conditionally shows management buttons based on user role:

```razor
@if (Model.IsPlatformAdmin)
{
    <a class="btn btn-success" asp-page="Add">
        <i class="bi bi-plus-lg"></i> Add Scope
    </a>
}
```

In the table rows:

```razor
@if (Model.IsPlatformAdmin)
{
    <div class="btn-group btn-group-sm" role="group">
        <a href="@(tenantPrefix)/Admin/Scopes/Edit/@s.Name" class="btn btn-outline-secondary">Edit</a>
        <a asp-page="Delete" asp-route-name="@s.Name" class="btn btn-outline-danger">Del</a>
    </div>
}
else
{
    <span class="badge text-bg-secondary">
        <i class="bi bi-eye me-1"></i>View Only
    </span>
}
```

### Defense in Depth

Even though UI buttons are hidden for tenant admins, the backend enforces authorization:

```csharp
public async Task<IActionResult> OnPostDeleteAsync(string name)
{
    // Backend validation - defense in depth
    var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
    if (!platformAdminResult.Succeeded)
    {
        return Forbid(); // Returns 403 if tenant admin tries to delete
    }
    // ... deletion logic
}
```

## Tenant Separation Summary

### What Tenant Admins CAN Do:
✅ **View** all available scopes
✅ **Assign** scopes to their tenant's clients (via Client configuration)
✅ **Request** scopes in authorization flows (standard OAuth2 behavior)

### What Tenant Admins CANNOT Do:
❌ **Create** new scopes (would pollute shared catalog)
❌ **Edit** existing scopes (would affect all tenants)
❌ **Delete** scopes (would break other tenants' clients)

### What Platform Admins CAN Do:
✅ Everything tenant admins can do
✅ **Create** new scopes for all tenants
✅ **Edit** scope descriptions and visibility
✅ **Delete** unused scopes (with safety check for client assignments)

## Usage Pattern

### Typical Workflow

1. **Platform Admin:** Creates standard scopes:
   ```
   openid, profile, email, offline_access, roles, api.read, api.write
   ```

2. **Tenant Admin (pop-app):** Configures client to use scopes:
   - Views available scopes via UI
   - Assigns `openid`, `profile`, `email` to their client

3. **End User:** Authorizes with client:
   - Requests: `openid profile email`
   - Receives tokens with these scopes
   - Uses tokens with APIs

4. **API (any tenant):** Validates tokens:
   - Checks for required scopes (e.g., `api.read`)
   - Works regardless of which tenant issued the token

## Migration Considerations

If future requirements need **tenant-specific scopes**, consider these options:

### Option 1: Namespaced Scopes (Recommended)
Keep scopes global but use tenant prefixes:
```
pop-app.custom-scope
default.admin-scope
```

### Option 2: Tenant-Scoped Scopes (Complex)
Add `TenantId` to Scope entity:
- Requires migration
- Needs scope resolution logic in token issuance
- Complicates API validation (must know issuing tenant)
- Not recommended unless absolutely necessary

### Option 3: Custom Claims (Alternative)
Use custom claims instead of scopes for tenant-specific permissions:
- Keep scopes for OAuth2 standard purposes
- Use claims for tenant-specific authorization
- Simpler to implement and validate

## Files Modified

### Backend Authorization
- `Admin/Scopes/Index.cshtml.cs` - Inherits TenantAwarePageModel, fixed redirects
- `Admin/Scopes/Add.cshtml.cs` - Inherits TenantAwarePageModel, fixed redirects
- `Admin/Scopes/Edit.cshtml.cs` - Inherits TenantAwarePageModel, fixed redirects

### UI Conditional Rendering
- `Admin/Scopes/Index.cshtml` - Hide Add button for tenant admins
- `Admin/Scopes/Index.cshtml` - Show "View Only" badge for tenant admins instead of Edit/Del buttons

## Testing Verification

- ✅ Build successful
- ✅ All 366 unit tests passing
- ✅ Tenant admins can view scopes but cannot see management buttons
- ✅ Platform admins see full management UI
- ✅ Tenant-aware redirects work correctly for all roles

## Related Documentation

- `docs/tenant-separation-roles-security-fix.md` - Tenant filtering for tenant-scoped entities
- `docs/tenant-aware-redirect-fixes.md` - Redirect pattern fixes
- `docs/multitenancy-security-completion-summary.md` - Overall multi-tenancy security

## Recommendations

1. **Document Scope Naming Convention**
   - Create guidelines for naming custom scopes
   - Consider prefixing tenant-specific needs (e.g., `custom.tenant-name.scope`)

2. **Scope Approval Workflow**
   - Consider implementing a request/approval workflow for custom scopes
   - Prevents scope sprawl

3. **Scope Lifecycle Management**
   - Document when scopes can be deprecated
   - Add "deprecated" flag instead of deleting

4. **API Documentation**
   - Document available scopes for API developers
   - Include scope purpose and required permissions
