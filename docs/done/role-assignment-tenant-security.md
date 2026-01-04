# Role Assignment Tenant Security Implementation

**Date**: October 5, 2025  
**Status**: ✅ Complete  
**Files Modified**: 2

## Problem Statement

The User → Role assignment page was not tenant-aware, creating security vulnerabilities:
- Users could be assigned roles from different tenants (cross-tenant assignments)
- Realms were not filtered by user's tenant
- Clients were not filtered by user's tenant
- No validation that roles, realms, and clients belonged to the same tenant as the user
- No tenant visibility in assignment tables

## Solution Implemented

### Security Enhancements

1. **Tenant Boundary Enforcement**
   - All realms filtered by user's tenant
   - All clients filtered by user's tenant
   - All roles filtered by user's tenant
   - Validation: Realm must belong to user's tenant
   - Validation: Role must belong to user's tenant AND selected realm
   - Validation: Client must belong to user's tenant

2. **Dual Assignment Types Protected**
   - **Realm Roles**: User + Realm + Role (all from same tenant)
   - **Client Roles**: User + Client + Role (all from same tenant)

3. **Tenant Visibility**
   - Alert banner shows user's tenant at top of page
   - Note: "Only realms, clients, and roles from this tenant can be assigned"
   - Tenant column added to both realm role and client role assignment tables
   - Tenant badge displayed for each assignment

### Technical Implementation

**Backend (`Pages/Admin/Users/Roles/Index.cshtml.cs`):**

```csharp
// Load user with tenant info
var userQuery = from u in db.Users.AsNoTracking()
                join t in db.Tenants on u.TenantId equals t.Id
                where u.Id == UserId
                select new { User = u, Tenant = t };

// Realms filtered by user's tenant
Realms = await db.Realms.AsNoTracking()
    .Where(r => r.TenantId == UserTenantId)
    .OrderBy(r => r.Name)
    .Select(r => new RealmVm(r.Id, r.Name))
    .ToListAsync();

// Clients filtered by user's tenant
Clients = await db.Clients.AsNoTracking()
    .Where(c => c.TenantId == UserTenantId)
    .OrderBy(c => c.ClientId)
    .Select(c => new ClientVm(c.Id, c.ClientId, c.ClientName))
    .ToListAsync();

// Roles filtered by user's tenant
Roles = await db.Roles.AsNoTracking()
    .Where(r => r.TenantId == UserTenantId)
    .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new { RoleId = r.Id, r.Name, RealmName = rl.Name, RealmId = rl.Id })
    .OrderBy(x => x.Name)
    .Select(x => new RoleVm(x.RoleId, x.Name, x.RealmName, x.RealmId))
    .ToListAsync();
```

**Validation on Add Realm Role:**

```csharp
// Get user's tenant
var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
if (user is null) return RedirectToPage("/Admin/Users/Index");

// Validate realm belongs to user's tenant
var realmValid = await db.Realms.AsNoTracking()
    .AnyAsync(r => r.Id == RealmAddRealmId && r.TenantId == user.TenantId);
if (!realmValid)
{
    ModelState.AddModelError(string.Empty, "Realm does not belong to user's tenant.");
    return await OnGetAsync();
}

// Validate role belongs to user's tenant AND the selected realm
var roleValid = await db.Roles.AsNoTracking()
    .AnyAsync(r => r.Id == RealmAddRoleId && r.TenantId == user.TenantId && r.RealmId == RealmAddRealmId);
if (!roleValid)
{
    ModelState.AddModelError(string.Empty, "Selected role does not belong to the selected realm or user's tenant.");
    return await OnGetAsync();
}
```

**Validation on Add Client Role:**

```csharp
// Validate client belongs to user's tenant
var client = await db.Clients.AsNoTracking()
    .FirstOrDefaultAsync(c => c.Id == ClientAddClientId && c.TenantId == user.TenantId);
if (client is null)
{
    ModelState.AddModelError(string.Empty, "Client does not belong to user's tenant.");
    return await OnGetAsync();
}

// Validate role belongs to user's tenant
var roleValid = await db.Roles.AsNoTracking()
    .AnyAsync(r => r.Id == ClientAddRoleId && r.TenantId == user.TenantId);
if (!roleValid)
{
    ModelState.AddModelError(string.Empty, "Role does not belong to user's tenant.");
    return await OnGetAsync();
}
```

**Frontend (`Pages/Admin/Users/Roles/Index.cshtml`):**

```razor
<!-- Tenant alert banner -->
<div class="alert alert-secondary mb-3">
    <i class="bi bi-building"></i> <strong>User Tenant:</strong> <span class="badge text-bg-info">@Model.TenantName</span>
    <div class="small mt-1">Only realms, clients, and roles from this tenant can be assigned.</div>
</div>

<!-- Realm role section with tenant filtering -->
<select class="form-select" asp-for="RealmAddRealmId">
    @if (!Model.Realms.Any())
    {
        <option value="">-- No realms available --</option>
    }
    else
    {
        <option value="">-- Select realm --</option>
        @foreach (var r in Model.Realms)
        {
            <option value="@r.Id">@r.Name</option>
        }
    }
</select>

<!-- Client role section with tenant filtering -->
<select class="form-select" asp-for="ClientAddClientId">
    @if (!Model.Clients.Any())
    {
        <option value="">-- No clients available --</option>
    }
    else
    {
        <option value="">-- Select client --</option>
        @foreach (var c in Model.Clients)
        {
            <option value="@c.Id">@c.ClientId @(!string.IsNullOrEmpty(c.ClientName) ? $"({c.ClientName})" : "")</option>
        }
    }
</select>

<!-- Add buttons disabled when no data available -->
<button class="btn btn-primary" type="submit" disabled="@(!Model.Realms.Any() || !Model.Roles.Any())">Add</button>
```

### Changes Summary

#### Record Types Updated
- `RealmAssignmentVm` - Added `TenantName` field
- `ClientAssignmentVm` - Added `TenantName` field
- `ClientVm` - Simplified to just `Id`, `ClientId`, `ClientName` (no RealmName needed)
- Added `RealmVm` - Lightweight record for realm dropdown

#### Properties Added
- `TenantName` - Display user's tenant name
- `UserTenantId` - Store user's tenant ID for filtering

#### Query Modifications
1. User query now JOINs with Tenants
2. Realms query filtered by `UserTenantId`
3. Clients query filtered by `UserTenantId`
4. Roles query filtered by `UserTenantId`
5. RealmAssignments query JOINs with Tenants to display tenant name
6. ClientAssignments query JOINs with Tenants to display tenant name

#### Security Validations
**OnPostAddRealmAsync:**
1. Validate user exists
2. Validate realm exists AND belongs to user's tenant
3. Validate role exists AND belongs to user's tenant AND selected realm
4. Only then create assignment

**OnPostAddClientAsync:**
1. Validate user exists
2. Validate client exists AND belongs to user's tenant
3. Validate role exists AND belongs to user's tenant
4. Only then create assignment

## Benefits

✅ **Security**: Prevents cross-tenant role assignments (both realm and client roles)  
✅ **Data Integrity**: Ensures roles, realms, clients, and users all belong to same tenant  
✅ **Visibility**: Admin can see tenant for each assignment in both tables  
✅ **Validation**: Clear error messages when selections violate tenant boundaries  
✅ **Empty State Handling**: Clear messages when no data available in tenant  
✅ **UI Consistency**: Tenant badge display matches other pages  

## Testing Checklist

### Realm Roles
- [ ] Verify only realms from user's tenant appear in dropdown
- [ ] Verify only roles from user's tenant appear in dropdown
- [ ] Verify cross-tenant realm assignment is rejected (security test)
- [ ] Verify role from different realm is rejected (security test)
- [ ] Verify assignment list shows tenant badges
- [ ] Verify "No realms available" shows correctly for new tenant

### Client Roles
- [ ] Verify only clients from user's tenant appear in dropdown
- [ ] Verify only roles from user's tenant appear in dropdown
- [ ] Verify cross-tenant client assignment is rejected (security test)
- [ ] Verify assignment list shows tenant badges
- [ ] Verify "No clients available" shows correctly for new tenant

### General
- [ ] Verify tenant alert banner shows correct tenant name
- [ ] Verify both Add buttons are disabled when no data available
- [ ] Verify empty state messages appear in both tables
- [ ] Verify remove operations work correctly

## Related Files

- `MrWhoOidc.WebAuth/Pages/Admin/Users/Roles/Index.cshtml.cs` - Backend logic
- `MrWhoOidc.WebAuth/Pages/Admin/Users/Roles/Index.cshtml` - Frontend UI
- `MrWhoOidc.Auth/Persistence/UserRealmRoleAssignment.cs` - Realm role entity
- `MrWhoOidc.Auth/Persistence/UserClientRoleAssignment.cs` - Client role entity

## Architecture Notes

This page handles TWO types of role assignments:
1. **Realm Roles** (UserRealmRoleAssignment): Direct realm-level roles
2. **Client Roles** (UserClientRoleAssignment): Client-specific roles

Both must respect tenant boundaries. The implementation ensures:
- Users can only be assigned roles/realms/clients within their own tenant
- Cross-tenant assignments are blocked at multiple validation layers
- UI only shows options from the user's tenant
- Audit trail via tenant badges in assignment tables

## Future Enhancements

- Consider adding bulk role assignment with tenant validation
- Add audit logging for cross-tenant assignment attempts
- Add filtering/search when many roles/clients exist in a tenant
- Consider cascading dropdowns (select realm → show only roles from that realm)
