# Critical Security Fix: Tenant Separation in Roles Index Page

## Date: October 11, 2025

## Severity: HIGH

## Problem
The Roles Index page (`Admin/Roles/Index.cshtml.cs`) was displaying **ALL roles from ALL tenants** to tenant admins, violating tenant isolation. A tenant admin logged into the `pop-app` tenant could see roles from the `Default Tenant` and other tenants.

## Root Cause
The tenant filtering logic had a critical flaw:

```csharp
// VULNERABLE CODE (Before Fix):
if (TenantId.HasValue)
{
    q = q.Where(x => x.Role.TenantId == TenantId.Value);
}
```

The problem: This only filtered by tenant when the `TenantId` query parameter was explicitly provided. For tenant admins (non-platform admins), `TenantId` would be `null`, so **no filtering was applied** - they saw all roles from all tenants.

## Impact
- **Data Exposure:** Tenant admins could view roles from other tenants
- **Tenant Isolation Breach:** Violated multi-tenancy security boundary
- **Compliance Risk:** Cross-tenant data visibility

## Fix Applied

Changed the filtering logic to explicitly differentiate between platform admins and tenant admins:

```csharp
// SECURE CODE (After Fix):
// Apply tenant filtering
if (IsPlatformAdmin)
{
    // Platform admins can optionally filter by tenant
    if (TenantId.HasValue)
    {
        q = q.Where(x => x.Role.TenantId == TenantId.Value);
    }
}
else
{
    // Tenant admins can ONLY see their tenant's roles
    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
    {
        Roles = Array.Empty<RoleRow>();
        return;
    }
    q = q.Where(x => x.Role.TenantId == currentTenantId.Value);
}
```

## Verification

### Other Admin Sections Audited ✅

All other admin index pages were checked and have **correct tenant filtering**:

1. **Clients** (`Admin/Clients/Index.cshtml.cs`) ✅
   - Has correct platform-admin vs tenant-admin filtering
   
2. **Users** (`Admin/Users/Index.cshtml.cs`) ✅
   - Has correct platform-admin vs tenant-admin filtering
   
3. **Providers** (`Admin/Providers/Index.cshtml.cs`) ✅
   - Has correct platform-admin vs tenant-admin filtering
   
4. **Realms** (`Admin/Realms/Index.cshtml.cs`) ✅
   - Has correct platform-admin vs tenant-admin filtering
   
5. **Scopes** (`Admin/Scopes/Index.cshtml.cs`) ✅
   - Correctly shows global/shared scopes (by design - scopes have no TenantId)
   - Only platform admins can delete scopes

### Testing
- ✅ Build successful
- ✅ All 366 unit tests passing
- ✅ Manual verification: Roles page now shows only current tenant's roles

## Pattern Reference

### Correct Tenant Filtering Pattern

All admin index pages should follow this pattern:

```csharp
// Check platform admin status
var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
IsPlatformAdmin = platformAdminResult.Succeeded;

// Build base query
var q = db.Entities.AsNoTracking()
    .Join(db.Tenants, entity => entity.TenantId, t => t.Id, (entity, t) => new { Entity = entity, Tenant = t });

// Apply tenant filtering
if (IsPlatformAdmin)
{
    // Platform admins can optionally filter by tenant (via TenantId query param)
    if (TenantId.HasValue)
    {
        q = q.Where(x => x.Entity.TenantId == TenantId.Value);
    }
    // If TenantId is null, show ALL tenants (platform admin feature)
}
else
{
    // Tenant admins ALWAYS filtered to their tenant - NO EXCEPTIONS
    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
    {
        // No tenant context, return empty for safety
        Entities = Array.Empty<EntityRow>();
        return;
    }
    q = q.Where(x => x.Entity.TenantId == currentTenantId.Value);
}
```

### Anti-Pattern (Vulnerable)

**NEVER** use this pattern - it creates a tenant isolation vulnerability:

```csharp
// ❌ VULNERABLE - Don't do this:
if (TenantId.HasValue)
{
    q = q.Where(x => x.Entity.TenantId == TenantId.Value);
}
// Problem: If TenantId is null, NO filtering is applied!
```

## Recommendations

### 1. Add Integration Test

Create a test that verifies tenant admins cannot see other tenants' data:

```csharp
[TestMethod]
public async Task TenantAdmin_CannotSeeOtherTenantRoles()
{
    // Arrange: Create roles in two different tenants
    var tenant1 = /* create tenant 1 */;
    var tenant2 = /* create tenant 2 */;
    var role1 = /* create role in tenant 1 */;
    var role2 = /* create role in tenant 2 */;
    
    // Act: Request roles as tenant1 admin
    var result = await /* fetch roles with tenant1 context */;
    
    // Assert: Should only see tenant1 roles
    Assert.IsTrue(result.Contains(role1));
    Assert.IsFalse(result.Contains(role2));
}
```

### 2. Add Automated Security Audit

Create a script that scans all admin Index pages for the anti-pattern:

```powershell
# Search for conditional tenant filtering without proper else clause
Get-ChildItem -Path "MrWhoOidc.WebAuth/Pages/Admin" -Recurse -Filter "Index.cshtml.cs" |
    Select-String -Pattern "if.*TenantId\.HasValue.*\n.*Where.*TenantId" -Context 10,10 |
    Where-Object { $_.Context -notmatch "else" }
```

### 3. Code Review Checklist

When reviewing admin pages that query tenant-scoped data, verify:

- [ ] Platform admin status is checked explicitly
- [ ] Tenant admins **always** have tenant filtering applied (no null checks that skip filtering)
- [ ] Early return with empty results if no tenant context exists
- [ ] TenantId query parameter only affects platform admin view

## Files Modified

- `MrWhoOidc.WebAuth/Pages/Admin/Roles/Index.cshtml.cs` - Added correct tenant filtering logic

## Related Documents

- `docs/tenant-aware-redirect-fixes.md` - Covers redirect issues (separate concern)
- `docs/multitenancy-security-completion-summary.md` - Overall multi-tenancy security
- `docs/admin-guide.md` - Admin UI documentation

## Lessons Learned

1. **Defense in Depth:** Even with authorization policies (`tenant-admin`), query-level filtering is essential
2. **Explicit Over Implicit:** Always explicitly handle both platform-admin and tenant-admin cases
3. **Fail Secure:** When tenant context is missing, return empty results rather than unfiltered data
4. **Pattern Consistency:** All admin pages should follow the same tenant filtering pattern
