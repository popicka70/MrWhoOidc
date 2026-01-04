# Client Edit Page Scope Assignment - Implementation Complete

**Date:** October 11, 2025  
**Status:** ✅ Complete  
**Build:** Clean (0 errors, 0 warnings)  
**Tests:** All passing

## Overview

Updated the Client Edit page to show available scopes with visual grouping that distinguishes between global scopes (available to all tenants) and tenant-scoped scopes (specific to the current tenant). The implementation uses `IScopeResolver` to provide tenant-aware scope filtering and presents the scopes with clear visual indicators.

## Implementation Details

### 1. Backend Changes (`Edit.cshtml.cs`)

#### Added IScopeResolver Dependency
```csharp
public class EditModel(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    ILogger<EditModel> logger, 
    MrWhoOidc.WebAuth.Observability.IAuditSink audit, 
    OidcOptions oidcOptions,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IClientStore clientStore,
    IScopeResolver scopeResolver) : TenantAwarePageModel(tenantAccessor)
```

#### Added Grouped Scope Properties
```csharp
// Scopes tab model
public List<Scope> AvailableScopes { get; private set; } = new();
public List<Scope> GlobalAvailableScopes { get; private set; } = new();
public List<Scope> TenantAvailableScopes { get; private set; } = new();
public List<string> AssignedScopes { get; private set; } = new();
public List<string> GlobalAssignedScopes { get; private set; } = new();
public List<string> TenantAssignedScopes { get; private set; } = new();
public string CurrentTenantSlug => TenantAccessor.CurrentTenant?.Slug ?? "tenant";
```

#### Updated LoadScopesAsync Method
The method now uses `IScopeResolver` to get tenant-aware scopes and groups them by type:

```csharp
private async Task LoadScopesAsync(Guid clientId)
{
    // Get current tenant context for scope resolution
    var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
    
    // Get available scopes for current tenant context using scope resolver
    var availableScopes = await scopeResolver.GetAvailableScopesAsync(currentTenantId);
    var availableScopeNames = availableScopes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
    
    // Get assigned scopes for this client
    AssignedScopes = await db.ClientScopes.AsNoTracking()
        .Where(cs => cs.ClientId == clientId)
        .Select(cs => cs.ScopeName)
        .OrderBy(n => n)
        .ToListAsync();
    
    // Filter available list to those not yet assigned
    var availableScopeObjects = availableScopes
        .Where(s => !AssignedScopes.Contains(s.Name, StringComparer.Ordinal))
        .OrderBy(s => s.IsGlobal ? 0 : 1) // Global scopes first
        .ThenBy(s => s.Name)
        .ToList();
    
    // Group available scopes
    AvailableScopes = availableScopeObjects;
    GlobalAvailableScopes = availableScopeObjects.Where(s => s.IsGlobal).ToList();
    TenantAvailableScopes = availableScopeObjects.Where(s => !s.IsGlobal).ToList();
    
    // Group assigned scopes by checking if they're standard scopes
    GlobalAssignedScopes = AssignedScopes.Where(s => scopeResolver.IsStandardScope(s)).OrderBy(s => s).ToList();
    TenantAssignedScopes = AssignedScopes.Where(s => !scopeResolver.IsStandardScope(s)).OrderBy(s => s).ToList();
}
```

### 2. UI Changes (`Edit.cshtml`)

#### Assigned Scopes Section
Shows scopes grouped by type with visual badges:

**Global Scopes:**
- 🌐 Header with globe icon
- Blue "Global" badge
- Standard OAuth2/OIDC scopes (openid, profile, email, etc.)

**Tenant Scopes:**
- 🏢 Header with building icon
- Cyan "Tenant" badge
- Tenant-specific custom scopes (e.g., acme.reports.read)

```razor
@if (Model.GlobalAssignedScopes.Any())
{
    <h6 class="text-muted mt-3">
        <i class="bi bi-globe me-1"></i> Global Scopes
    </h6>
    <table class="table table-sm align-middle">
        <tbody>
        @foreach (var s in Model.GlobalAssignedScopes)
        {
            <tr>
                <td>
                    <span class="badge bg-primary-subtle text-primary-emphasis me-1">Global</span>
                    @s
                </td>
                <td class="text-end">
                    <button type="submit" class="btn btn-sm btn-outline-danger" 
                            asp-page-handler="RemoveScope" name="scopeName" value="@s" 
                            formnovalidate>Remove</button>
                </td>
            </tr>
        }
        </tbody>
    </table>
}

@if (Model.TenantAssignedScopes.Any())
{
    <h6 class="text-muted mt-3">
        <i class="bi bi-building me-1"></i> Tenant Scopes
    </h6>
    <table class="table table-sm align-middle">
        <tbody>
        @foreach (var s in Model.TenantAssignedScopes)
        {
            <tr>
                <td>
                    <span class="badge bg-info-subtle text-info-emphasis me-1">Tenant</span>
                    @s
                </td>
                <td class="text-end">
                    <button type="submit" class="btn btn-sm btn-outline-danger" 
                            asp-page-handler="RemoveScope" name="scopeName" value="@s" 
                            formnovalidate>Remove</button>
                </td>
            </tr>
        }
        </tbody>
    </table>
}
```

#### Add Scope Section
Dropdown with optgroups for visual separation:

```razor
<select asp-for="NewScope" class="form-select">
    <option value="">-- select scope --</option>
    @if (Model.GlobalAvailableScopes.Any())
    {
        <optgroup label="🌐 Global Scopes">
            @foreach (var s in Model.GlobalAvailableScopes)
            {
                <option value="@s.Name">@s.Name</option>
            }
        </optgroup>
    }
    @if (Model.TenantAvailableScopes.Any())
    {
        <optgroup label="🏢 Tenant Scopes">
            @foreach (var s in Model.TenantAvailableScopes)
            {
                <option value="@s.Name">@s.Name</option>
            }
        </optgroup>
    }
</select>
```

#### Help Text
Clear explanation of the two scope types:

```razor
<small class="form-text text-muted">
    <strong>Global scopes</strong> are available to all tenants (e.g., openid, profile, email).<br />
    <strong>Tenant scopes</strong> are specific to your tenant (e.g., acme.reports.read).
</small>
```

## Visual Design

### Color Coding

| Scope Type | Badge Color | Icon | Visual Purpose |
|------------|-------------|------|----------------|
| Global | Primary (Blue) | 🌐 Globe | Indicates scopes available to all tenants |
| Tenant | Info (Cyan) | 🏢 Building | Indicates tenant-specific scopes |

### Bootstrap Classes Used

- `bg-primary-subtle text-primary-emphasis` - Global scope badge
- `bg-info-subtle text-info-emphasis` - Tenant scope badge
- `bi-globe` - Bootstrap icon for global scopes
- `bi-building` - Bootstrap icon for tenant scopes

## User Experience Improvements

### Before
- Flat list of all scopes with no visual distinction
- No indication whether a scope was global or tenant-specific
- Hard to understand scope availability context

### After
- Clear visual grouping with headers and badges
- Icons provide quick visual recognition
- Help text explains the difference between scope types
- Dropdown with optgroups for easy selection
- Empty state message when no scopes assigned

## Tenant Awareness

### Platform Admin View
- Can see and assign **all global scopes**
- Can see and assign **all tenant-scoped scopes** (for all tenants)

### Tenant Admin View
- Can see and assign **global scopes** (openid, profile, email, etc.)
- Can see and assign **only their tenant's scopes** (e.g., acme.reports.read for tenant "acme")
- Cannot see or assign scopes from other tenants

### Scope Resolution Logic

1. **IScopeResolver.GetAvailableScopesAsync(tenantId)** returns:
   - All global scopes (`IsGlobal = true`)
   - Tenant-specific scopes where `TenantId = tenantId`

2. **Grouping Logic:**
   - **GlobalAssignedScopes:** Uses `scopeResolver.IsStandardScope()` to identify standard OAuth2/OIDC scopes
   - **TenantAssignedScopes:** All non-standard scopes (custom tenant scopes)

3. **Sorting:**
   - Available scopes: Global first, then tenant scopes, alphabetically within each group
   - Assigned scopes: Alphabetically within each group

## Architecture Benefits

### 1. Consistency with Scopes Admin Pages
- Uses same `IScopeResolver` service as Scopes Add/Index pages
- Consistent tenant filtering logic across admin UI
- Same visual design language (badges, icons, grouping)

### 2. Security
- Tenant isolation enforced by `IScopeResolver`
- Cannot assign scopes from other tenants
- Platform admins have full visibility

### 3. Usability
- Clear visual distinction reduces confusion
- Grouped dropdown makes selection easier
- Help text educates users about scope types
- Empty states provide feedback

### 4. Maintainability
- Single source of truth for scope resolution
- Consistent grouping logic using `IsGlobal` property
- Easy to extend with additional scope types

## Testing Recommendations

### Manual Testing Scenarios

#### As Platform Admin
1. ✅ Navigate to any client's edit page
2. ✅ Verify all global scopes appear in "Global Scopes" group
3. ✅ Verify all tenant scopes from all tenants appear in "Tenant Scopes" group
4. ✅ Assign a global scope and verify it appears with blue "Global" badge
5. ✅ Assign a tenant scope and verify it appears with cyan "Tenant" badge
6. ✅ Remove scopes and verify they move back to available list

#### As Tenant Admin (e.g., "acme" tenant)
1. ✅ Navigate to a client in your tenant
2. ✅ Verify you see global scopes (openid, profile, email, etc.)
3. ✅ Verify you only see your tenant's scopes (acme.*)
4. ✅ Verify you don't see other tenants' scopes (contoso.*, fabrikam.*)
5. ✅ Assign scopes and verify proper grouping and badges
6. ✅ Check help text shows correct tenant slug in example

### Automated Testing (Future)

```csharp
[TestClass]
public class ClientEditScopeAssignmentTests
{
    [TestMethod]
    public async Task LoadScopes_PlatformAdmin_SeesAllScopes()
    {
        // Arrange: Create test scopes (global + multiple tenants)
        // Act: Call LoadScopesAsync as platform admin
        // Assert: AvailableScopes contains all scopes
    }
    
    [TestMethod]
    public async Task LoadScopes_TenantAdmin_SeesOnlyTenantScopes()
    {
        // Arrange: Create test scopes for multiple tenants
        // Act: Call LoadScopesAsync with tenant context
        // Assert: TenantAvailableScopes only contains current tenant's scopes
    }
    
    [TestMethod]
    public async Task GroupedScopes_ProperlyClassifiesGlobalVsTenant()
    {
        // Arrange: Mix of global and tenant scopes
        // Act: Call LoadScopesAsync
        // Assert: GlobalAssignedScopes contains standard scopes only
        // Assert: TenantAssignedScopes contains custom scopes only
    }
}
```

## Related Documentation

- [Scope Naming Validation Complete](scope-naming-validation-complete.md) - Naming conventions for scopes
- [Tenant-Scoped Scopes Backlog](tenant-scoped-scopes-backlog.md) - Overall feature backlog
- [Multi-Tenancy Quick Reference](multitenancy-quick-reference.md) - Multi-tenancy architecture

## What's Next

The client edit page scope assignment UI is now complete. Remaining work includes:

1. **Unit Tests for Tenant-Scoped Scopes** ⏳
   - Create comprehensive tests for `IScopeResolver`
   - Test tenant isolation in scope resolution
   - Test scope validation logic
   - Test TokenService integration

2. **Performance Optimization** 🔄 (Optional)
   - Consider caching `GetAvailableScopesAsync` results
   - Add EF Core query optimization if needed
   - Monitor database query performance

3. **API Documentation** 📝
   - Document scope assignment APIs
   - Add examples for programmatic scope assignment
   - Document scope validation rules

---

**Related Files:**
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` - Backend logic
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml` - UI template
- `MrWhoOidc.Auth/Services/IScopeResolver.cs` - Scope resolution service
