# Tenant UI Display Checklist

## Summary
This document identifies all UI locations where tenant information needs to be displayed for proper multi-tenant separation of realms, clients, and users.

## Database Entities with TenantId

The following entities have `TenantId` properties and need tenant-aware UI:

### Core Entities
1. **User** (`TenantId: Guid`)
2. **Realm** (`TenantId: Guid`)
3. **Role** (`TenantId: Guid`)
4. **Client** (`TenantId: Guid`)
5. **SigningKey** (`TenantId: Guid?` - nullable for backward compat)
6. **AuthorizationCode** (`TenantId: Guid`)
7. **Consent** (`TenantId: Guid`)
8. **Token** (`TenantId: Guid`)
9. **PushedAuthorizationRequest** (`TenantId: Guid`)
10. **Registration** (`TenantId: Guid`)
11. **IdentityProvider** (`TenantId: Guid`)
12. **BackchannelLogoutNotification** (`TenantId: Guid`)
13. **QrLoginSession** (`TenantId: Guid`)

### Non-Tenant Entities (No TenantId)
- **Scope** (global across all tenants - no TenantId)
- **ClientScope** (junction table - inherits tenant via Client)
- **UserAlternativeEmail** (inherits tenant via User)
- **UserClientAssignment** (inherits tenant via User/Client)
- **UserRoleAssignment** (inherits tenant via User/Role/Client)
- **UserRealmRoleAssignment** (inherits tenant via User/Role/Realm)
- **UserClientRoleAssignment** (inherits tenant via User/Role/Client)
- **ClientIdentityProvider** (junction table - inherits tenant via Client/Provider)
- **IdentityProviderClaimMapping** (inherits tenant via IdentityProvider)
- **IdentityProviderKey** (inherits tenant via IdentityProvider)
- **ClientJwksHistory** (inherits tenant via Client)
- **ExternalIdentity** (inherits tenant via User)
- **LogoutRedirectReference** (inherits tenant via Client)
- **RevocationAudit** (inherits tenant via Client)

---

## UI Pages Requiring Tenant Display

### 1. Users Management (`/Admin/Users/`)

#### **Index** (`/Admin/Users/Index.cshtml`)
- **Grid columns:**
  - ✅ Username
  - ✅ Email
  - ✅ Name
  - ✅ Created
  - **❌ MISSING: Tenant** ← ADD COLUMN
- **Actions:** Edit, View details, Manage roles/clients

#### **Edit** (`/Admin/Users/Edit.cshtml`)
- **Detail view:**
  - ✅ Username
  - ✅ Email
  - ✅ Name
  - **❌ MISSING: Tenant (read-only display)** ← ADD FIELD

#### **Add** (`/Admin/Users/Add.cshtml`)
- **Form fields:**
  - ✅ Username
  - ✅ Email
  - ✅ Name
  - ✅ Password
  - **❌ MISSING: Tenant (dropdown selector)** ← ADD FIELD

#### **Roles/Index** (`/Admin/Users/Roles/Index.cshtml`)
- **Realm Roles Grid:**
  - ✅ Realm
  - ✅ Role
  - ✅ Active
  - **❌ MISSING: Tenant** ← ADD COLUMN (for Realm)
- **Client Roles Grid:**
  - ✅ Client
  - ✅ Realm
  - ✅ Role
  - ✅ Active
  - **❌ MISSING: Tenant** ← ADD COLUMN (for Client)

#### **Clients/Index** (`/Admin/Users/Clients/Index.cshtml`)
- **Grid columns:**
  - ✅ Client
  - ✅ Realm
  - ✅ Active
  - **❌ MISSING: Tenant** ← ADD COLUMN

#### **Emails/Index** (`/Admin/Users/Emails/Index.cshtml`)
- **Grid columns:**
  - ✅ Email
  - ✅ Verified
  - ✅ Verified at
  - **Note:** No tenant needed (inherits from parent User)

#### **Linked/Index** (`/Admin/Users/Linked/Index.cshtml`)
- **Grid columns:**
  - ✅ Provider
  - ✅ Issuer
  - ✅ Subject
  - ✅ Created
  - ✅ Last seen
  - **❌ MISSING: Tenant** ← ADD COLUMN (for Provider)

---

### 2. Clients Management (`/Admin/Clients/`)

#### **Index** (`/Admin/Clients/Index.cshtml`)
- **Grid columns:**
  - ✅ Client ID
  - ✅ Name
  - ✅ Realm
  - ✅ PKCE
  - ✅ Consent
  - ✅ Status (JWKS, PAR)
  - **❌ MISSING: Tenant** ← ADD COLUMN

#### **Edit** (`/Admin/Clients/Edit.cshtml`)
- **Detail view:**
  - ✅ Client ID
  - ✅ Name
  - ✅ Realm (dropdown)
  - ✅ All settings tabs (General, Redirect URIs, Providers, Scopes, Keys, Introspection, M2M, OBO, Tools)
  - **❌ MISSING: Tenant (read-only display at top)** ← ADD FIELD

#### **Add** (`/Admin/Clients/Add.cshtml`)
- **Form fields:**
  - ✅ Client ID
  - ✅ Name
  - ✅ Realm (dropdown)
  - **❌ MISSING: Tenant (dropdown selector)** ← ADD FIELD
  - **Note:** Realm dropdown should be filtered by selected tenant

---

### 3. Realms Management (`/Admin/Realms/`)

#### **Index** (`/Admin/Realms/Index.cshtml`)
- **Grid columns:**
  - ✅ Name
  - ✅ Display Name
  - ✅ Created
  - **❌ MISSING: Tenant** ← ADD COLUMN

#### **Edit** (`/Admin/Realms/Edit.cshtml`)
- **Detail view:**
  - ✅ Name
  - ✅ Display Name
  - **❌ MISSING: Tenant (read-only display)** ← ADD FIELD

#### **Add** (`/Admin/Realms/Add.cshtml`)
- **Form fields:**
  - ✅ Name
  - ✅ Display Name
  - **❌ MISSING: Tenant (dropdown selector)** ← ADD FIELD

---

### 4. Roles Management (`/Admin/Roles/`)

#### **Index** (`/Admin/Roles/Index.cshtml`)
- **Grid columns:**
  - ✅ Name
  - ✅ Realm
  - ✅ Active
  - **❌ MISSING: Tenant** ← ADD COLUMN
- **Filter form:**
  - ✅ Realm dropdown
  - ✅ Search
  - **❌ MISSING: Tenant dropdown filter** ← ADD FILTER

#### **Edit** (`/Admin/Roles/Edit.cshtml`)
- **Detail view:**
  - ✅ Name
  - ✅ Realm (dropdown)
  - ✅ Active
  - **❌ MISSING: Tenant (read-only display)** ← ADD FIELD

#### **Add** (`/Admin/Roles/Add.cshtml`)
- **Form fields:**
  - ✅ Name
  - ✅ Realm (dropdown)
  - ✅ Active
  - **❌ MISSING: Tenant (dropdown selector)** ← ADD FIELD
  - **Note:** Realm dropdown should be filtered by selected tenant

---

### 5. Identity Providers Management (`/Admin/Providers/`)

#### **Index** (`/Admin/Providers/Index.cshtml`)
- **Grid (draggable list):**
  - ✅ Display Name
  - ✅ Name • Type • Enabled/Disabled • Default
  - **❌ MISSING: Tenant** ← ADD DISPLAY

#### **Edit** (`/Admin/Providers/Edit.cshtml`)
- **Detail view (tabs: General, Form Editor, JSON Configuration):**
  - ✅ Name
  - ✅ Display Name
  - ✅ Type
  - ✅ Enabled
  - ✅ Is Default
  - ✅ Sort Order
  - ✅ Logo
  - **❌ MISSING: Tenant (read-only display)** ← ADD FIELD

#### **Add** (`/Admin/Providers/Add.cshtml`)
- **Form fields:**
  - ✅ Name
  - ✅ Display Name
  - ✅ Type
  - **❌ MISSING: Tenant (dropdown selector)** ← ADD FIELD

#### **Details** (`/Admin/Providers/Details.cshtml`)
- **Detail view:**
  - ✅ All provider configuration details
  - **❌ MISSING: Tenant** ← ADD DISPLAY

---

### 6. Provider Keys Management (`/Admin/ProviderKeys/`)

#### **Index** (`/Admin/ProviderKeys/Index.cshtml`)
- **Grid columns:**
  - Keys for a specific provider
  - **❌ MISSING: Tenant** ← ADD COLUMN
  - **Note:** Tenant inherited from IdentityProvider, should be displayed

---

### 7. Provider Claim Mappings (`/Admin/ProviderClaimMappings/`)

#### **Index** (`/Admin/ProviderClaimMappings/Index.cshtml`)
- **Grid columns:**
  - Claim mappings for a provider
  - **❌ MISSING: Tenant** ← ADD COLUMN (inherited from Provider)

#### **Edit** (`/Admin/ProviderClaimMappings/Edit.cshtml`)
- **Detail view:**
  - **❌ MISSING: Tenant** ← ADD DISPLAY (inherited from Provider)

---

### 8. Provider Mappings (`/Admin/ProviderMappings/`)

#### **Index** (`/Admin/ProviderMappings/Index.cshtml`)
- **Grid columns:**
  - ✅ Client
  - ✅ Provider
  - ✅ Enabled
  - ✅ Default
  - ✅ Auto
  - ✅ ACR
  - ✅ Order
  - **❌ MISSING: Tenant** ← ADD COLUMN (for both Client and Provider)

---

### 9. Scopes Management (`/Admin/Scopes/`)

#### **Index** (`/Admin/Scopes/Index.cshtml`)
- **Grid columns:**
  - ✅ Name
  - ✅ Description
  - ✅ Exposed
  - **✅ NO TENANT NEEDED** (Scopes are global)

#### **Edit/Add** (`/Admin/Scopes/Edit.cshtml`, `/Admin/Scopes/Add.cshtml`)
- **✅ NO TENANT NEEDED** (Scopes are global)

---

### 10. Registrations Management (`/Admin/Registrations/`)

#### **Index** (`/Admin/Registrations/Index.cshtml`)
- **Grid columns:**
  - ✅ Email
  - ✅ Name
  - ✅ Client
  - ✅ State
  - ✅ Created
  - ✅ Approved/Rejected
  - **❌ MISSING: Tenant** ← ADD COLUMN

---

### 11. Back-channel Logout Outbox (`/Admin/Backchannel/`)

#### **Index** (`/Admin/Backchannel/Index.cshtml`)
- **Grid columns:**
  - ✅ Created
  - ✅ Client
  - ✅ Target
  - ✅ Status
  - ✅ Attempts
  - ✅ Last HTTP
  - ✅ Last Error
  - **❌ MISSING: Tenant** ← ADD COLUMN

---

### 12. Platform Admin - Tenants (`/PlatformAdmin/Tenants/`)

#### **Index** (`/PlatformAdmin/Tenants/Index.cshtml`)
- **✅ Already tenant-focused** (lists tenants)

#### **Create/Edit** (`/PlatformAdmin/Tenants/Create.cshtml`, `/PlatformAdmin/Tenants/Edit.cshtml`)
- **✅ Already tenant-focused** (manages tenant properties)

---

## Implementation Strategy

### Phase 1: Add Tenant Display to List Views (Grids)
**Priority: HIGH**

1. **Users/Index**: Add "Tenant" column
2. **Clients/Index**: Add "Tenant" column
3. **Realms/Index**: Add "Tenant" column
4. **Roles/Index**: Add "Tenant" column + tenant filter dropdown
5. **Providers/Index**: Add tenant display in list items
6. **Registrations/Index**: Add "Tenant" column
7. **Backchannel/Index**: Add "Tenant" column
8. **ProviderMappings/Index**: Add "Tenant" column
9. **Users/Roles/Index**: Add "Tenant" column to both realm and client role grids
10. **Users/Clients/Index**: Add "Tenant" column
11. **Users/Linked/Index**: Add "Tenant" column (for Provider)

### Phase 2: Add Tenant Display to Detail/Edit Views
**Priority: HIGH**

1. **Users/Edit**: Add read-only tenant display
2. **Clients/Edit**: Add read-only tenant display at top
3. **Realms/Edit**: Add read-only tenant display
4. **Roles/Edit**: Add read-only tenant display
5. **Providers/Edit**: Add read-only tenant display
6. **Providers/Details**: Add tenant display

### Phase 3: Add Tenant Selector to Create Views
**Priority: HIGH**

1. **Users/Add**: Add tenant dropdown (required)
2. **Clients/Add**: Add tenant dropdown (required) + filter Realm dropdown by tenant
3. **Realms/Add**: Add tenant dropdown (required)
4. **Roles/Add**: Add tenant dropdown (required) + filter Realm dropdown by tenant
5. **Providers/Add**: Add tenant dropdown (required)

### Phase 4: Add Tenant Filters
**Priority: MEDIUM**

1. **Roles/Index**: Add tenant dropdown filter (currently only has Realm filter)
2. **Users/Index**: Add tenant dropdown filter
3. **Clients/Index**: Add tenant dropdown filter
4. **Realms/Index**: Add tenant dropdown filter
5. **All other list views**: Consider tenant filter where appropriate

### Phase 5: Backend Model Updates
**Priority: HIGH** (Required for all UI changes)

For each PageModel:
1. Load tenant information from database
2. Join with Tenants table to get tenant Name/DisplayName
3. Filter queries by current admin's accessible tenants (if not platform admin)
4. Add tenant validation on POST handlers

### Phase 6: Cascading Filters
**Priority: MEDIUM**

When tenant is selected:
1. **Clients/Add + Edit**: Filter Realm dropdown to only show realms in selected tenant
2. **Roles/Add + Edit**: Filter Realm dropdown to only show realms in selected tenant
3. **User role assignments**: Filter Client/Realm dropdowns by tenant
4. **Provider mappings**: Ensure Client and Provider are in same tenant

---

## Data Model Considerations

### Current Tenant Context
- Platform admins can see/manage all tenants
- Regular admins should only see their own tenant(s)
- Need to determine current user's tenant scope in every page

### Tenant Lookup Performance
- Consider caching tenant names (Dictionary<Guid, string>)
- Use JOIN queries to fetch tenant info with main entity
- Add indexes on TenantId columns if not already present

### Validation Rules
- Cross-tenant references must be prevented
- When assigning User → Client: both must be in same tenant
- When assigning User → Role → Realm: all must be in same tenant
- When mapping Client → Provider: both must be in same tenant

---

## UI/UX Conventions

### Tenant Display Format
- **Grid column header**: "Tenant"
- **Badge style**: `<span class="badge text-bg-info">@tenantName</span>`
- **Read-only display**: Show as labeled field (e.g., "Tenant: Acme Corp")
- **Dropdown selector**: Standard form-select with label "Tenant"

### Tenant Column Position
- Place tenant column after primary identifier (Name/ID)
- Before status/metadata columns (Created, Updated)
- Suggestion: ID/Name → **Tenant** → Realm → Other fields

### Filtering
- Add tenant filter as first filter control (before realm/search)
- "All Tenants" option for platform admins
- Auto-set to current user's tenant for regular admins

---

## Code Page Models to Update

### Backend Updates Required

1. **IndexModel classes** (for grids):
   - Join with Tenants table
   - Add `TenantName` property to view models
   - Filter by user's tenant scope

2. **EditModel classes** (for details):
   - Load tenant info
   - Add read-only `TenantName` property

3. **AddModel classes** (for create):
   - Add `List<SelectListItem> TenantOptions`
   - Validate tenant selection
   - Set TenantId on new entity

4. **All POST handlers**:
   - Validate tenant ownership
   - Prevent cross-tenant operations
   - Return appropriate error messages

---

## Testing Checklist

After implementation:
- [ ] Platform admin can see all tenants in all grids
- [ ] Regular admin can only see their tenant(s)
- [ ] Tenant column sorts correctly
- [ ] Tenant filters work correctly
- [ ] Cannot create cross-tenant assignments
- [ ] Cannot edit entities from other tenants
- [ ] Tenant cascading filters work (Realm dropdown filtered by Tenant)
- [ ] All validation errors display correctly
- [ ] Performance acceptable with tenant JOINs

---

## Priority Summary

**Must Have (Blocker for multi-tenant production):**
- All "Add" forms must have tenant selector
- All "Index" grids must display tenant
- All "Edit" views must display tenant (read-only)
- Backend validation to prevent cross-tenant operations

**Should Have (Important for usability):**
- Tenant filters on all major grids
- Cascading filters (Realm filtered by Tenant)
- Proper error messages for tenant validation failures

**Nice to Have (Future enhancement):**
- Tenant switching UI for platform admins
- Tenant-based color coding/theming
- Tenant usage statistics dashboard

---

## Notes

- **SigningKey.TenantId is nullable**: Need to handle backward compatibility (existing keys with null TenantId)
- **Scopes are global**: No tenant display needed
- **Junction tables inherit tenant**: No direct tenant display needed, but validate relationships
- **Current tenant context**: Need middleware/service to determine user's current tenant scope

