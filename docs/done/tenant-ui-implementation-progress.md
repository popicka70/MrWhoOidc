# Tenant UI Implementation Progress

## Completed Changes

### ✅ Users/Index
**Backend (`Pages/Admin/Users/Index.cshtml.cs`):**
- ✅ Added `UserRow` record with `TenantId` and `TenantName`
- ✅ Added `TenantOptions` property for filter dropdown
- ✅ Added `TenantId` bind property for filtering
- ✅ Modified query to JOIN with Tenants table
- ✅ Added tenant filtering logic
- ✅ Updated Delete handler to preserve tenant filter

**Frontend (`Pages/Admin/Users/Index.cshtml`):**
- ✅ Added tenant dropdown filter
- ✅ Added "Tenant" column to grid
- ✅ Display tenant as badge

---

## Remaining Implementation Tasks

### Phase 1: Critical List Views (Priority: HIGH)

#### 1. Clients/Index
**Backend changes needed:**
```csharp
// In Pages/Admin/Clients/Index.cshtml.cs
public sealed record ClientRow(Guid Id, string ClientId, string? ClientName, string RealmName, Guid TenantId, string TenantName, bool RequirePkce, bool RequireConsent, bool HasJwks, bool RequirePar);

public List<SelectListItem> TenantOptions { get; private set; } = new();

[BindProperty(SupportsGet = true)]
public Guid? TenantId { get; set; }

// In LoadAsync():
var tenants = await db.Tenants.AsNoTracking()
    .Where(t => t.Status == TenantStatus.Active)
    .OrderBy(t => t.Name)
    .ToListAsync();
TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));

Clients = await db.Clients.AsNoTracking()
    .Join(db.Tenants, c => c.TenantId, t => t.Id, (c, t) => new { Client = c, Tenant = t })
    .Join(db.Realms, x => x.Client.RealmId, r => r.Id, (x, r) => new { x.Client, x.Tenant, Realm = r })
    .Where(x => !TenantId.HasValue || x.Client.TenantId == TenantId.Value)
    .OrderBy(x => x.Client.ClientId)
    .Select(x => new ClientRow(
        x.Client.Id,
        x.Client.ClientId,
        x.Client.ClientName,
        x.Realm.Name,
        x.Client.TenantId,
        x.Tenant.Name,
        x.Client.RequirePkce,
        x.Client.RequireConsent,
        !string.IsNullOrEmpty(x.Client.PublicJwksJson) || !string.IsNullOrEmpty(x.Client.PublicJwksUri),
        x.Client.RequirePar
    ))
    .ToListAsync();
```

**Frontend changes needed:**
- Add tenant filter dropdown (similar to Users/Index)
- Add "Tenant" column after "Name"
- Display as badge: `<span class="badge text-bg-info">@c.TenantName</span>`

#### 2. Realms/Index
**Backend changes needed:**
```csharp
// In Pages/Admin/Realms/Index.cshtml.cs
public sealed record RealmRow(Guid Id, string Name, string? DisplayName, DateTimeOffset CreatedAt, Guid TenantId, string TenantName);

public IReadOnlyList<RealmRow> Realms { get; private set; } = Array.Empty<RealmRow>();
public List<SelectListItem> TenantOptions { get; private set; } = new();

[BindProperty(SupportsGet = true)]
public Guid? TenantId { get; set; }

public async Task OnGetAsync()
{
    var tenants = await db.Tenants.AsNoTracking()
        .Where(t => t.Status == TenantStatus.Active)
        .OrderBy(t => t.Name)
        .ToListAsync();
    TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
    TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));

    var q = db.Realms.AsNoTracking()
        .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { Realm = r, Tenant = t });

    if (TenantId.HasValue)
    {
        q = q.Where(x => x.Realm.TenantId == TenantId.Value);
    }

    Realms = await q
        .OrderBy(x => x.Realm.Name)
        .Select(x => new RealmRow(
            x.Realm.Id,
            x.Realm.Name,
            x.Realm.DisplayName,
            x.Realm.CreatedAt,
            x.Realm.TenantId,
            x.Tenant.Name
        ))
        .ToListAsync();
}
```

**Frontend changes needed:**
- Add tenant filter dropdown
- Add "Tenant" column after "Name"

#### 3. Roles/Index
**Backend changes needed:**
```csharp
// In Pages/Admin/Roles/Index.cshtml.cs
public sealed record RoleRow(Guid Id, string Name, Guid RealmId, string RealmName, Guid TenantId, string TenantName, bool IsActive);

public IReadOnlyList<RoleRow> Roles { get; private set; } = Array.Empty<RoleRow>();
public List<SelectListItem> TenantOptions { get; private set; } = new();

[BindProperty(SupportsGet = true)]
public Guid? TenantId { get; set; }

// Update OnGetAsync to filter by tenant and JOIN with Tenants table
public async Task OnGetAsync()
{
    // Load tenants for filter
    var allTenants = await db.Tenants.AsNoTracking()
        .Where(t => t.Status == TenantStatus.Active)
        .OrderBy(t => t.Name)
        .ToListAsync();
    TenantOptions = allTenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
    TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));

    // Filter realms by tenant if selected
    var realmQuery = db.Realms.AsNoTracking().AsQueryable();
    if (TenantId.HasValue)
    {
        realmQuery = realmQuery.Where(r => r.TenantId == TenantId.Value);
    }
    Realms = await realmQuery.OrderBy(r => r.Name).ToListAsync();

    // Build query with tenant and realm JOINs
    var q = db.Roles.AsNoTracking()
        .Join(db.Tenants, role => role.TenantId, t => t.Id, (role, t) => new { Role = role, Tenant = t })
        .Join(db.Realms, x => x.Role.RealmId, r => r.Id, (x, r) => new { x.Role, x.Tenant, Realm = r });

    if (TenantId.HasValue)
    {
        q = q.Where(x => x.Role.TenantId == TenantId.Value);
    }
    
    if (RealmId is Guid rid)
    {
        q = q.Where(x => x.Role.RealmId == rid);
    }
    
    if (!string.IsNullOrWhiteSpace(Search))
    {
        var s = Search.Trim();
        q = q.Where(x => x.Role.Name.Contains(s));
    }

    Roles = await q
        .OrderBy(x => x.Role.Name)
        .Select(x => new RoleRow(
            x.Role.Id,
            x.Role.Name,
            x.Role.RealmId,
            x.Realm.Name,
            x.Role.TenantId,
            x.Tenant.Name,
            x.Role.IsActive
        ))
        .ToListAsync();
}
```

**Frontend changes needed:**
- Add tenant filter dropdown (FIRST filter, before Realm)
- Update realm filter to be tenant-aware
- Add "Tenant" column

#### 4. Providers/Index
**Backend changes needed:**
```csharp
// In Pages/Admin/Providers/Index.cshtml.cs
public sealed record ProviderRow(Guid Id, string Name, string? DisplayName, IdentityProviderType Type, bool Enabled, bool IsDefault, int SortOrder, Guid TenantId, string TenantName);

public IReadOnlyList<ProviderRow> Providers { get; private set; } = Array.Empty<ProviderRow>();
public List<SelectListItem> TenantOptions { get; private set; } = new();

[BindProperty(SupportsGet = true)]
public Guid? TenantId { get; set; }

// Update OnGetAsync()
```

**Frontend changes needed:**
- Add tenant filter
- Display tenant in list items

#### 5. Registrations/Index
**Backend changes needed:**
- Add tenant JOIN and display
- Add TenantId filter

**Frontend changes needed:**
- Add tenant column

#### 6. Backchannel/Index
**Backend changes needed:**
- Add tenant JOIN
- Add tenant filter

**Frontend changes needed:**
- Add tenant column

---

### Phase 2: Detail/Edit Views (Priority: HIGH)

#### Users/Edit
**Backend:**
```csharp
public string TenantName { get; private set; } = string.Empty;

public async Task OnGetAsync(Guid id)
{
    var data = await db.Users
        .Where(u => u.Id == id)
        .Join(db.Tenants, u => u.TenantId, t => t.Id, (u, t) => new { User = u, Tenant = t })
        .FirstOrDefaultAsync();
    
    if (data is null) return;
    
    UserId = data.User.Id;
    TenantName = data.Tenant.Name;
    Input = new EditInput
    {
        Username = data.User.Username,
        Email = data.User.Email,
        Name = data.User.Name
    };
}
```

**Frontend:**
```html
<div class="alert alert-info mb-3">
    <strong>Tenant:</strong> <span class="badge text-bg-info">@Model.TenantName</span>
</div>
```

#### Clients/Edit
**Backend:**
- Add `TenantName` property
- Load tenant name in OnGetAsync
- Display read-only at top of form

**Frontend:**
- Add tenant display banner at top

#### Realms/Edit
- Same pattern as Clients/Edit

#### Roles/Edit
- Same pattern

#### Providers/Edit
- Same pattern

---

### Phase 3: Add/Create Views (Priority: HIGH)

#### Users/Add
**Backend:**
```csharp
public List<SelectListItem> TenantOptions { get; private set; } = new();

public async Task OnGetAsync()
{
    var tenants = await db.Tenants.AsNoTracking()
        .Where(t => t.Status == TenantStatus.Active)
        .OrderBy(t => t.Name)
        .ToListAsync();
    TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
}

public class AddInput
{
    [Required]
    public Guid TenantId { get; set; }
    
    [Required, StringLength(200)]
    public string Username { get; set; } = string.Empty;
    
    [EmailAddress, StringLength(256)]
    public string? Email { get; set; }
    
    [StringLength(200)]
    public string? Name { get; set; }
}

public async Task<IActionResult> OnPostAsync()
{
    if (!ModelState.IsValid)
    {
        await OnGetAsync();
        return Page();
    }
    
    // Validate tenant exists and is active
    var tenantExists = await db.Tenants.AnyAsync(t => t.Id == Input.TenantId && t.Status == TenantStatus.Active);
    if (!tenantExists)
    {
        ModelState.AddModelError("Input.TenantId", "Invalid tenant selected.");
        await OnGetAsync();
        return Page();
    }
    
    var username = Input.Username.Trim();
    
    // Check uniqueness within tenant
    if (await db.Users.AnyAsync(u => u.TenantId == Input.TenantId && u.Username == username))
    {
        ModelState.AddModelError("Input.Username", "Username already exists in this tenant.");
        await OnGetAsync();
        return Page();
    }
    
    var email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email!.Trim();
    var normalized = EmailNormalizer.NormalizeForLookup(email);
    if (!string.IsNullOrEmpty(normalized) && await db.Users.AnyAsync(u => u.TenantId == Input.TenantId && u.NormalizedEmail == normalized))
    {
        ModelState.AddModelError("Input.Email", "Email already exists in this tenant.");
        await OnGetAsync();
        return Page();
    }
    
    db.Users.Add(new User
    {
        TenantId = Input.TenantId,
        Username = username,
        Email = email,
        Name = Input.Name,
        EmailVerified = false,
        HashAlgorithm = "argon2id",
        PasswordHash = string.Empty
    });
    
    await db.SaveChangesAsync();
    return RedirectToPage("Index", new { TenantId = Input.TenantId });
}
```

**Frontend:**
```html
<div class="mb-3">
    <label asp-for="Input.TenantId" class="form-label">Tenant <span class="text-danger">*</span></label>
    <select asp-for="Input.TenantId" asp-items="Model.TenantOptions" class="form-select">
        <option value="">-- Select Tenant --</option>
    </select>
    <span asp-validation-for="Input.TenantId" class="text-danger"></span>
</div>
```

#### Clients/Add
**Backend:**
- Add TenantOptions dropdown
- Add TenantId to ClientInput
- Filter Realm dropdown by selected tenant (JavaScript or separate endpoint)
- Set TenantId on new Client entity
- Validate cross-tenant references

**Frontend:**
- Add tenant dropdown FIRST
- Add JavaScript to reload realm dropdown when tenant changes

#### Realms/Add
- Add tenant selector
- Validate tenant

#### Roles/Add
- Add tenant selector
- Filter realms by tenant
- Validate tenant-realm relationship

#### Providers/Add
- Add tenant selector

---

### Phase 4: Related Entity Pages

#### Users/Roles/Index
**Changes:**
- Add "Tenant" column to realm roles grid
- Add "Tenant" column to client roles grid
- Show tenant of realm/client in assignments

#### Users/Clients/Index
**Changes:**
- Add "Tenant" column
- Show tenant for each client assignment

#### Users/Linked/Index
**Changes:**
- Add "Tenant" column for Provider

#### ProviderMappings/Index
**Changes:**
- Add "Tenant" column
- Show tenant for both Client and Provider
- Validate same-tenant relationships

---

## Implementation Notes

### Tenant Validation Rules
1. **Cross-tenant references must be prevented**
   - When assigning User → Client: both must be in same tenant
   - When assigning User → Role → Realm: all must be in same tenant
   - When mapping Client → Provider: both must be in same tenant

2. **Uniqueness scopes**
   - Usernames must be unique within a tenant (not globally)
   - Client IDs should be unique within a tenant
   - Realm names should be unique within a tenant

### Performance Considerations
- Use JOIN queries instead of separate lookups
- Consider caching tenant list (changes infrequently)
- Add indexes on TenantId columns if not present
- Use AsNoTracking() for read-only queries

### UI/UX Guidelines
- **Tenant badge color**: `text-bg-info` (blue)
- **Filter position**: Tenant filter should be FIRST
- **Cascading filters**: When tenant changes, reload dependent dropdowns (Realms, etc.)
- **Platform admin**: Can see "All Tenants" option
- **Regular admin**: Pre-select and lock to their tenant

---

## Testing Checklist
After each implementation:
- [ ] Platform admin can see all tenants
- [ ] Tenant filter works
- [ ] Tenant column displays correctly
- [ ] Cannot create cross-tenant relationships
- [ ] Cannot edit entities from other tenants
- [ ] Tenant selection required on create forms
- [ ] Cascading filters work (Realm filtered by Tenant)
- [ ] Delete/Edit operations preserve tenant filter
- [ ] Validation messages are clear

---

## Files Modified So Far
1. ✅ `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml.cs`
2. ✅ `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml`

## Files Still To Modify
### Backend (.cshtml.cs files):
- [ ] Clients/Index.cshtml.cs
- [ ] Clients/Add.cshtml.cs
- [ ] Clients/Edit.cshtml.cs
- [ ] Realms/Index.cshtml.cs
- [ ] Realms/Add.cshtml.cs
- [ ] Realms/Edit.cshtml.cs
- [ ] Roles/Index.cshtml.cs
- [ ] Roles/Add.cshtml.cs
- [ ] Roles/Edit.cshtml.cs
- [ ] Providers/Index.cshtml.cs
- [ ] Providers/Add.cshtml.cs
- [ ] Providers/Edit.cshtml.cs
- [ ] Registrations/Index.cshtml.cs
- [ ] Backchannel/Index.cshtml.cs
- [ ] ProviderMappings/Index.cshtml.cs
- [ ] Users/Add.cshtml.cs
- [ ] Users/Edit.cshtml.cs
- [ ] Users/Roles/Index.cshtml.cs
- [ ] Users/Clients/Index.cshtml.cs
- [ ] Users/Linked/Index.cshtml.cs

### Frontend (.cshtml files):
- [ ] Clients/Index.cshtml
- [ ] Clients/Add.cshtml
- [ ] Clients/Edit.cshtml
- [ ] Realms/Index.cshtml
- [ ] Realms/Add.cshtml
- [ ] Realms/Edit.cshtml
- [ ] Roles/Index.cshtml
- [ ] Roles/Add.cshtml
- [ ] Roles/Edit.cshtml
- [ ] Providers/Index.cshtml
- [ ] Providers/Add.cshtml
- [ ] Providers/Edit.cshtml
- [ ] Registrations/Index.cshtml
- [ ] Backchannel/Index.cshtml
- [ ] ProviderMappings/Index.cshtml
- [ ] Users/Add.cshtml
- [ ] Users/Edit.cshtml
- [ ] Users/Roles/Index.cshtml
- [ ] Users/Clients/Index.cshtml
- [ ] Users/Linked/Index.cshtml

**Total files to modify: ~40 files**

---

## Next Steps
1. Continue with Clients/Index (most critical)
2. Then Realms/Index
3. Then Roles/Index
4. Then all Add forms (users can't create entities without tenant selection)
5. Then Edit forms (users need to see which tenant an entity belongs to)
6. Finally, related entity pages

Would you like me to continue with the next batch of implementations?
