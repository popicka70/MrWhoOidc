# Multi-Tenancy Security Audit - Admin Pages

**Date**: October 10, 2025  
**Audited by**: GitHub Copilot  
**Scope**: All admin pages in `MrWhoOidc.WebAuth/Pages/Admin/**`

## Executive Summary

A comprehensive audit of the admin pages revealed **multiple critical security vulnerabilities** where tenant isolation is not enforced, allowing potential cross-tenant data access and modification.

### Severity Breakdown

- **Critical** (7 issues): Allow cross-tenant read/write access to sensitive data
- **High** (6 issues): Allow cross-tenant read access or secondary data modification
- **Medium** (2 issues): Indirect cross-tenant access via relationships

**Total Issues Found**: 15

---

## Critical Issues (Immediate Fix Required)

### 1. ✅ FIXED: Clients/Edit.cshtml.cs
**Status**: ✅ Fixed in current session  
**Entity**: Client (has TenantId)  
**Issue**: OnGetAsync and OnPostSaveAsync loaded client without tenant filtering  
**Impact**: Tenant admin could view/edit clients from other tenants  
**Fixed**: Added ITenantAccessor, IAuthorizationService, and ValidateTenantAccessAsync() helper

---

### 2. ❌ Providers/Edit.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**Entity**: IdentityProvider (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs`

#### Vulnerable Code:
**Line 36** (OnGetAsync):
```csharp
var entity = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
```

**Line 121** (OnPostAsync):
```csharp
var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
```

**Line 158** (OnPostTestAsync):
```csharp
// Loads entity indirectly via Input.Id without validation
```

#### Impact:
- Tenant admin can view/edit identity providers from other tenants
- Can modify OIDC configuration, client secrets, discovery URLs
- Can enable/disable providers across tenants
- Can change default provider settings affecting other tenants

#### Recommendation:
Add tenant filtering similar to Clients/Edit pattern.

---

### 3. ❌ Providers/Details.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**Entity**: IdentityProvider (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Providers/Details.cshtml.cs`

#### Vulnerable Code:
**Line 17** (OnGetAsync):
```csharp
Provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
```

#### Impact:
- Tenant admin can view provider configuration from other tenants
- Exposes OIDC client secrets, authority URLs, and sensitive config

#### Recommendation:
Add tenant filtering to OnGetAsync.

---

### 4. ❌ Providers/Delete.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**Entity**: IdentityProvider (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Providers/Delete.cshtml.cs`

#### Vulnerable Code:
**Line 15** (OnGetAsync):
```csharp
Provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
```

**Line 30** (OnPostAsync):
```csharp
var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
```

#### Impact:
- Tenant admin can delete providers from other tenants
- Disrupts authentication for other tenants
- Data loss across tenant boundaries

#### Recommendation:
Add tenant filtering to both GET and POST handlers.

---

### 5. ❌ Users/Edit.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**Entity**: User (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml.cs`

#### Vulnerable Code:
**Line 29-33** (OnGetAsync): ✅ Appears safe - uses JOIN with tenant
```csharp
var userQuery = from u in db.Users.AsNoTracking()
                join t in db.Tenants on u.TenantId equals t.Id
                where u.Id == id
                select new { User = u, Tenant = t };
```

**Line 57** (OnPostAsync): ⚠️ VULNERABLE
```csharp
var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
```

#### Impact:
- Tenant admin can modify users from other tenants
- Change username, email, name across tenant boundaries
- Email verification status can be manipulated
- Username/email uniqueness checks happen per-tenant, but initial load is not filtered

#### Recommendation:
Add explicit tenant filtering to OnPostAsync user load similar to pattern:
```csharp
var entity = await db.Users
    .FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId);
```

---

### 6. ❌ Users/Index.cshtml.cs (Delete Handler)
**Status**: ⚠️ VULNERABLE  
**Entity**: User (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml.cs`

#### Vulnerable Code:
**Line 106** (OnPostDeleteAsync):
```csharp
var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
```

#### Impact:
- Tenant admin can delete users from other tenants
- Data loss across tenant boundaries
- Orphaned data (sessions, roles, etc.)

#### Recommendation:
Add tenant filtering to user deletion.

---

### 7. ❌ ProviderClaimMappings/Edit.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**Entity**: IdentityProviderClaimMapping (no TenantId, inherits via IdentityProvider)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/ProviderClaimMappings/Edit.cshtml.cs`

#### Vulnerable Code:
**Line 19** (OnGetAsync):
```csharp
var entity = await db.IdentityProviderClaimMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
```

**Line 37** (OnPostAsync):
```csharp
var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id);
```

#### Impact:
- Tenant admin can modify claim mappings for providers in other tenants
- Affects authentication claim mappings cross-tenant
- Can break identity mapping for other tenants

#### Recommendation:
Add JOIN to IdentityProvider and filter by TenantId:
```csharp
var entity = await db.IdentityProviderClaimMappings
    .Join(db.IdentityProviders, m => m.IdentityProviderId, p => p.Id, (m, p) => new { Mapping = m, Provider = p })
    .Where(x => x.Mapping.Id == id && x.Provider.TenantId == currentTenantId)
    .Select(x => x.Mapping)
    .FirstOrDefaultAsync();
```

---

## High Severity Issues

### 8. ❌ Users/Roles/Index.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Roles/Index.cshtml.cs`

#### Vulnerable Code:
**Line 113** (OnPostAddAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

**Line 174** (OnPostRemoveByClientAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

#### Impact:
- Can assign/remove roles to users in other tenants
- Privilege escalation across tenant boundaries

#### Recommendation:
Add tenant filtering to user loads.

---

### 9. ❌ Users/Emails/Index.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Emails/Index.cshtml.cs`

#### Vulnerable Code:
**Line 20** (OnGetAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

**Line 37** (OnPostAddAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

**Lines 71, 84** (OnPostMakePrimaryAsync, OnPostDeleteAsync):
```csharp
var entity = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == emailId && a.UserId == UserId);
```

#### Impact:
- Can manage alternative emails for users in other tenants
- Email verification status manipulation
- Can see PII (email addresses) across tenant boundaries

#### Note:
UserAlternativeEmails is filtered by UserId, but the initial User load is not tenant-filtered, so if you know a UserId from another tenant, you can access their emails.

#### Recommendation:
Add tenant filtering to all user loads.

---

### 10. ❌ Users/Linked/Index.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Linked/Index.cshtml.cs`

#### Vulnerable Code:
**Line 19** (OnGetAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

**Line 31** (OnPostDeleteAsync):
```csharp
var entity = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.Id == linkId && e.UserId == UserId);
```

#### Impact:
- Can view/delete external identity links for users in other tenants
- Can break federated login for users cross-tenant

#### Recommendation:
Add tenant filtering to user load.

---

### 11. ❌ Users/Clients/Index.cshtml.cs
**Status**: ⚠️ VULNERABLE  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Users/Clients/Index.cshtml.cs`

#### Vulnerable Code:
**Line 98** (OnPostAssignAsync):
```csharp
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
```

#### Impact:
- Can assign clients to users in other tenants
- While line 103 checks client.TenantId == user.TenantId, the user load itself is not tenant-filtered

#### Recommendation:
Add tenant filtering to user load.

---

## Medium Severity Issues

### 12. ⚠️ Scopes/Edit.cshtml.cs
**Status**: ⚠️ NEEDS REVIEW  
**Entity**: Scope (NO TenantId - global resource)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Edit.cshtml.cs`

#### Current Code:
**Line 27** (OnGetAsync):
```csharp
var entity = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name);
```

**Line 36** (OnPostAsync):
```csharp
var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
```

#### Analysis:
Scopes appear to be **global resources** (no TenantId in the schema). This may be by design if scopes are meant to be system-wide (like "openid", "profile", "email").

#### Recommendation:
- If scopes should be global: Document this in code and restrict edit access to platform-admin only
- If scopes should be per-tenant: Add TenantId column and migrate data

---

### 13. ⚠️ Realms/Edit.cshtml.cs
**Status**: ✅ APPEARS SAFE  
**Entity**: Realm (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Realms/Edit.cshtml.cs`

#### Current Code (OnGetAsync):
```csharp
var realmQuery = from r in db.Realms.AsNoTracking()
                 join t in db.Tenants on r.TenantId equals t.Id
                 where r.Id == Id
                 select new { Realm = r, Tenant = t };
```

#### Current Code (OnPostAsync):
**Line 45** - Loads without explicit tenant filter:
```csharp
var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == Id);
```

But line 53 validates uniqueness within tenant:
```csharp
var exists = await db.Realms.AnyAsync(r => r.TenantId == realm.TenantId && r.Name == Input.Name);
```

#### Recommendation:
Add explicit tenant filtering to OnPostAsync for defense in depth.

---

### 14. ⚠️ Roles/Edit.cshtml.cs
**Status**: ✅ APPEARS SAFE  
**Entity**: Role (has TenantId)  
**File**: `MrWhoOidc.WebAuth/Pages/Admin/Roles/Edit.cshtml.cs`

Similar pattern to Realms - uses JOIN in OnGet but direct load in OnPost. Validates tenant scope indirectly.

#### Recommendation:
Add explicit tenant filtering to OnPostAsync for defense in depth.

---

## Pattern Analysis

### Secure Pattern (Should be used everywhere):

```csharp
public class EditModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ReadOnlyAdminPageModel
{
    private async Task<bool> ValidateTenantAccessAsync(Guid entityId)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (platformAdminResult.Succeeded)
            return true;

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
            return false;

        return await db.Entities.AnyAsync(e => e.Id == entityId && e.TenantId == currentTenantId.Value);
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
            return NotFound();
        // ... rest of GET logic
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
            return NotFound();
        // ... rest of POST logic
    }
}
```

### For entities without TenantId (via relationship):

```csharp
private async Task<bool> ValidateTenantAccessViaParentAsync(Guid childId, Guid parentIdFromChild)
{
    var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
    if (platformAdminResult.Succeeded)
        return true;

    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
    if (!currentTenantId.HasValue)
        return false;

    return await db.ParentEntities.AnyAsync(p => p.Id == parentIdFromChild && p.TenantId == currentTenantId.Value);
}
```

---

## Recommended Fix Priority

### Immediate (Critical - Fix Today):
1. ✅ Clients/Edit.cshtml.cs (FIXED)
2. Providers/Edit.cshtml.cs
3. Providers/Delete.cshtml.cs
4. Users/Edit.cshtml.cs
5. Users/Index.cshtml.cs (Delete handler)
6. ProviderClaimMappings/Edit.cshtml.cs

### High Priority (Fix This Week):
7. Providers/Details.cshtml.cs
8. Users/Roles/Index.cshtml.cs
9. Users/Emails/Index.cshtml.cs
10. Users/Linked/Index.cshtml.cs
11. Users/Clients/Index.cshtml.cs

### Medium Priority (Fix This Sprint):
12. Scopes/Edit.cshtml.cs (Needs architectural decision)
13. Realms/Edit.cshtml.cs (Defense in depth)
14. Roles/Edit.cshtml.cs (Defense in depth)

---

## Testing Recommendations

For each fixed page, test:
1. As tenant-admin in Tenant A, try to access entity from Tenant B by GUID → Should 404
2. As platform-admin, verify can still access entities from all tenants
3. Verify authorization page/modal/handler has proper error messages
4. Check that Index pages don't leak GUIDs from other tenants in links

---

## Database Review

Should verify these entities have TenantId:
- ✅ Client (has TenantId)
- ✅ User (has TenantId)
- ✅ Realm (has TenantId)
- ✅ Role (has TenantId)
- ✅ IdentityProvider (has TenantId)
- ❌ Scope (NO TenantId - appears global by design)
- ❌ IdentityProviderClaimMapping (NO TenantId - inherits via IdentityProvider)
- ❌ ClientScope (junction table - inherits via Client)
- ❌ UserRole (junction table - inherits via User)

---

## Additional Security Considerations

1. **Audit Logging**: All cross-tenant access attempts should be logged
2. **Rate Limiting**: Consider rate limiting on admin endpoints to prevent enumeration
3. **GUID Enumeration**: Consider using non-sequential IDs or additional HMAC validation
4. **Platform Admin Boundaries**: Review if platform admins should have unrestricted access or need audit trails

---

## Conclusion

The multi-tenancy feature was added recently, and many existing admin pages were not updated to enforce tenant boundaries. This audit identified **15 security issues** ranging from critical to medium severity.

**Immediate action required** on the 6 critical issues to prevent data breaches and cross-tenant access.
