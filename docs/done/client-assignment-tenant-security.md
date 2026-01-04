# Client Assignment Tenant Security Implementation

**Date**: October 5, 2025  
**Status**: ✅ Complete  
**Files Modified**: 2

## Problem Statement

The User → Client assignment page was not tenant-aware, which created security vulnerabilities:
- Users could be assigned to clients from different tenants (cross-tenant assignment)
- No validation that client and realm belonged to the same tenant as the user
- Poor UX: Client was selected first, realm second (backwards flow)
- No tenant visibility in the assignment list

## Solution Implemented

### Security Enhancements

1. **Tenant Boundary Enforcement**
   - All realms filtered by user's tenant
   - All clients filtered by user's tenant
   - Validation: Client must belong to user's tenant
   - Validation: Realm must belong to user's tenant
   - Validation: Client must belong to selected realm

2. **Correct Selection Flow**
   - **Realm selected FIRST** (filtered by user's tenant)
   - **Client selected SECOND** (filtered by selected realm)
   - Client dropdown is disabled until realm is selected
   - Auto-submit on realm change to load appropriate clients

3. **Tenant Visibility**
   - Alert banner shows user's tenant at top of page
   - Note: "Only clients from this tenant can be assigned"
   - Tenant column added to assignments grid
   - Tenant badge displayed for each assignment

### Technical Implementation

**Backend (`Pages/Admin/Users/Clients/Index.cshtml.cs`):**

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

// Clients filtered by user's tenant AND selected realm
var clientQuery = db.Clients.AsNoTracking()
    .Where(c => c.TenantId == UserTenantId);

if (RealmId.HasValue)
{
    clientQuery = clientQuery.Where(c => c.RealmId == RealmId.Value);
}
```

**Validation on Add:**

```csharp
// Validate client belongs to user's tenant
var client = await db.Clients.AsNoTracking()
    .FirstOrDefaultAsync(c => c.Id == ClientId && c.TenantId == user.TenantId);
if (client is null)
{
    // Security violation - reject
    return await OnGetAsync();
}

// Validate realm belongs to user's tenant
var realmValid = await db.Realms.AsNoTracking()
    .AnyAsync(r => r.Id == RealmId.Value && r.TenantId == user.TenantId);
if (!realmValid)
{
    // Security violation - reject
    return await OnGetAsync();
}

// Validate client belongs to selected realm
if (client.RealmId != RealmId.Value)
{
    // Invalid assignment - reject
    return await OnGetAsync();
}
```

**Frontend (`Pages/Admin/Users/Clients/Index.cshtml`):**

```razor
<!-- Tenant alert banner -->
<div class="alert alert-secondary mb-3">
    <i class="bi bi-building"></i> <strong>User Tenant:</strong> <span class="badge text-bg-info">@Model.TenantName</span>
    <div class="small mt-1">Only clients from this tenant can be assigned.</div>
</div>

<!-- Realm selector (FIRST) -->
<select class="form-select" asp-for="RealmId" onchange="this.form.submit()">
    <option value="">-- Select realm first --</option>
    @foreach (var r in Model.Realms)
    {
        <option value="@r.Id">@r.Name</option>
    }
</select>

<!-- Client selector (SECOND) - disabled until realm selected -->
<select class="form-select" asp-for="ClientId" 
        disabled="@(!Model.RealmId.HasValue || !Model.Clients.Any())">
    @if (!Model.RealmId.HasValue)
    {
        <option value="">-- Select realm first --</option>
    }
    else if (!Model.Clients.Any())
    {
        <option value="">-- No clients in this realm --</option>
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

<!-- Add button disabled until valid selection -->
<button class="btn btn-primary" type="submit" 
        disabled="@(!Model.RealmId.HasValue || !Model.Clients.Any())">Add</button>
```

### Changes Summary

#### Record Types Updated
- `AssignmentVm` - Added `TenantName` field
- `ClientVm` - Changed from `RealmName` to `RealmId` (for filtering logic)
- Added `RealmVm` - Lightweight record for realm dropdown

#### Properties Added
- `TenantName` - Display user's tenant name
- `UserTenantId` - Store user's tenant ID for filtering
- `RealmId` - Changed to `BindProperty(SupportsGet = true)` to support query string filtering

#### Query Modifications
1. User query now JOINs with Tenants
2. Realms query filtered by `UserTenantId`
3. Clients query filtered by `UserTenantId` and optional `RealmId`
4. Assignments query JOINs with Tenants to display tenant name

#### Security Validations in OnPostAddAsync
1. Validate user exists
2. Validate client exists AND belongs to user's tenant
3. Validate realm exists AND belongs to user's tenant
4. Validate client belongs to selected realm
5. Only then create assignment

## Benefits

✅ **Security**: Prevents cross-tenant client assignments  
✅ **Data Integrity**: Ensures client, realm, and user all belong to same tenant  
✅ **UX Improvement**: Realm-first selection is logical and intuitive  
✅ **Visibility**: Admin can see tenant for each assignment  
✅ **Cascading Filters**: Client list updates based on realm selection  
✅ **Validation**: Clear messages when selections are invalid  

## Testing Checklist

- [ ] Verify only realms from user's tenant appear in dropdown
- [ ] Verify client dropdown is disabled until realm is selected
- [ ] Verify only clients from selected realm appear in client dropdown
- [ ] Verify cross-tenant assignment is rejected (security test)
- [ ] Verify client from different realm is rejected (security test)
- [ ] Verify assignment list shows tenant badges
- [ ] Verify realm filter persists after add/delete operations
- [ ] Verify "No clients in this realm" message shows correctly

## Related Files

- `MrWhoOidc.WebAuth/Pages/Admin/Users/Clients/Index.cshtml.cs` - Backend logic
- `MrWhoOidc.WebAuth/Pages/Admin/Users/Clients/Index.cshtml` - Frontend UI
- `MrWhoOidc.Auth/Persistence/UserClientAssignment.cs` - Entity definition

## Future Enhancements

- Consider adding audit logging for cross-tenant assignment attempts
- Add admin warning if user has assignments from multiple tenants (data migration scenario)
- Add bulk assignment feature with tenant validation
