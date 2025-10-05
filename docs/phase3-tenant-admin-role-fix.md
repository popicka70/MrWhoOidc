# Phase 3: Tenant Admin Role Fix

## Problem: "Access Denied" Due to Role Mismatch

### Symptoms
After implementing tenant-aware navigation and authentication redirects, users were correctly navigating to `/t/pop-app/Admin/Clients` but getting "Access Denied" (on the correct tenant-aware page, not 404).

### Root Cause

**Configuration vs Seeding Mismatch**:

1. **Authorization Handler** (`TenantAdminAuthorizationHandler`) looks for:
   - Role: `tenant-admin`
   - Realm: `default`
   - Tenant: Current tenant from `ITenantAccessor`

2. **Seeder** (`TenantSeedingService.SeedSampleTenantAsync`) was creating:
   - Role: `admin` (not `tenant-admin`!)
   - Realm: `admin` (not `default`!)
   - Tenant: Correct ✅

**Result**: Authorization check always failed because:
```csharp
// Handler looks for this:
SELECT * FROM UserRoleAssignments ura
JOIN Roles r ON ura.RoleId = r.Id
JOIN Realms rl ON r.RealmId = rl.Id
WHERE r.Name = 'tenant-admin'
  AND rl.Name = 'default'
  AND rl.TenantId = {currentTenantId}
  AND ura.UserId = {userId}
  AND ura.IsActive = true
  AND r.IsActive = true
-- Found: 0 rows ❌

// But seeder created this:
-- Role: 'admin', Realm: 'admin', Tenant: correct
-- Doesn't match the query above!
```

### The Fix

Modified `TenantSeedingService.SeedSampleTenantAsync` to create the correct role structure:

**File**: `MrWhoOidc.WebAuth/Services/TenantSeedingService.cs`

#### Change 1: Create tenant-admin Role in Default Realm

```csharp
// BEFORE: Only created 'admin' role in 'admin' realm
var adminRole = new Role
{
    Name = "admin",
    RealmId = adminRealm.Id,
    TenantId = tenant.Id,
    IsActive = true
};
_db.Roles.Add(adminRole);

// AFTER: Create both roles
// Create tenant-admin role in default realm (used for tenant admin authorization)
var tenantAdminRole = new Role
{
    Name = "tenant-admin",
    RealmId = defaultRealm.Id,  // ← default realm!
    TenantId = tenant.Id,
    IsActive = true
};
_db.Roles.Add(tenantAdminRole);

// Create admin role in admin realm (legacy, kept for compatibility)
var adminRole = new Role
{
    Name = "admin",
    RealmId = adminRealm.Id,
    TenantId = tenant.Id,
    IsActive = true
};
_db.Roles.Add(adminRole);
```

#### Change 2: Assign Both Roles to Admin User

```csharp
// BEFORE: Only assigned 'admin' role in 'admin' realm
var roleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = adminRole.Id,
    ClientId = adminClient.Id,
    RealmId = adminRealm.Id,
    IsActive = true
};
_db.UserRoleAssignments.Add(roleAssignment);

// AFTER: Assign both roles
// Assign tenant-admin role to admin user (in default realm)
var tenantAdminRoleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = tenantAdminRole.Id,
    ClientId = adminClient.Id,
    RealmId = defaultRealm.Id,  // ← default realm!
    IsActive = true
};
_db.UserRoleAssignments.Add(tenantAdminRoleAssignment);

// Also assign legacy admin role (in admin realm)
var adminRoleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = adminRole.Id,
    ClientId = adminClient.Id,
    RealmId = adminRealm.Id,
    IsActive = true
};
_db.UserRoleAssignments.Add(adminRoleAssignment);
```

### Configuration Reference

**TenantAdminAuthOptions** defaults (in `TenantAdminAuthOptions.cs`):
```csharp
public string RealmName { get; set; } = "default";
public string TenantAdminRoleName { get; set; } = "tenant-admin";
```

These can be overridden in `appsettings.json`:
```json
{
  "TenantAdminAuth": {
    "RealmName": "default",
    "TenantAdminRoleName": "tenant-admin"
  }
}
```

### Authorization Logic

From `TenantAdminAuthorizationHandler.cs`:

```csharp
protected override async Task HandleRequirementAsync(
    AuthorizationHandlerContext context,
    TenantAdminRequirement requirement)
{
    // 1. Extract user ID from claims
    var sub = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!Guid.TryParse(sub, out var userId))
        return;

    // 2. Check if user is platform admin (automatically granted)
    var isPlatformAdmin = await _db.UserRoleAssignments
        .Join(_db.Roles, ...)
        .Join(_db.Realms, ...)
        .AnyAsync(x => x.a.UserId == userId
                       && x.r.Name == "platform-admin"
                       && x.rl.Name == "platform");
    if (isPlatformAdmin)
    {
        context.Succeed(requirement);
        return;
    }

    // 3. Get current tenant from request context
    var tenantId = _tenantAccessor.CurrentTenant?.TenantId;
    if (tenantId == null)
        return;  // No tenant context - cannot proceed

    // 4. Check if user has tenant-admin role in current tenant's default realm
    var isTenantAdmin = await _db.UserRoleAssignments
        .Join(_db.Roles, ...)
        .Join(_db.Realms, ...)
        .AnyAsync(x => x.a.UserId == userId
                       && x.a.IsActive
                       && x.r.IsActive
                       && x.r.Name == "tenant-admin"       // ← Role name
                       && x.rl.Name == "default"           // ← Realm name
                       && x.rl.TenantId == tenantId);     // ← Current tenant

    if (isTenantAdmin)
        context.Succeed(requirement);
}
```

### Realm Structure After Seeding

Each tenant now has:

1. **default** realm:
   - Role: `tenant-admin` (used for admin portal authorization)
   - Purpose: Tenant-scoped admin access
   - Admin user has this role ✅

2. **admin** realm:
   - Role: `admin` (legacy, compatibility)
   - Purpose: Historical/compatibility
   - Admin user has this role ✅

### Testing Steps

1. **Reset database** (drop existing tenants or reseed):
   ```powershell
   docker compose down -v
   docker compose up -d --build
   ```

2. **Create new tenant** via Platform Admin UI:
   - Go to: `https://localhost:8443/PlatformAdmin`
   - Seed tenant: `pop-app` / "Pop App"
   - Admin email: `admin@pop.app` (or custom)
   - Admin password: `Admin123!` (or custom)

3. **Login as tenant admin**:
   - Go to: `https://localhost:8443/DiscoverTenant`
   - Enter: `admin@pop.app`
   - Login via: `https://localhost:8443/t/pop-app/login`

4. **Access admin pages**:
   - Should redirect to: `https://localhost:8443/t/pop-app/`
   - Click "Clients" → `https://localhost:8443/t/pop-app/Admin/Clients`
   - Page should load ✅ (no "Access Denied")

### Database Verification

Check role assignment:
```sql
SELECT 
    t.Slug as TenantSlug,
    u.Email as UserEmail,
    rl.Name as RealmName,
    r.Name as RoleName,
    c.ClientId,
    ura.IsActive as AssignmentActive,
    r.IsActive as RoleActive
FROM UserRoleAssignments ura
JOIN Users u ON ura.UserId = u.Id
JOIN Roles r ON ura.RoleId = r.Id
JOIN Realms rl ON r.RealmId = rl.Id
JOIN Tenants t ON rl.TenantId = t.Id
JOIN Clients c ON ura.ClientId = c.Id
WHERE u.Email = 'admin@pop.app'
  AND t.Slug = 'pop-app';
```

**Expected output**:
| TenantSlug | UserEmail | RealmName | RoleName | ClientId | AssignmentActive | RoleActive |
|------------|-----------|-----------|----------|----------|------------------|------------|
| pop-app | admin@pop.app | **default** | **tenant-admin** | pop-app-admin | true | true |
| pop-app | admin@pop.app | admin | admin | pop-app-admin | true | true |

The **first row** is what the authorization handler checks!

### Summary of All Fixes

Phase 3 complete fix chain:

1. ✅ **Navigation Fix**: Layout menu links use `ITenantAccessor.CurrentTenant.Slug`
2. ✅ **Login Redirect Fix**: Login redirects to `/t/{slug}/` instead of `/`
3. ✅ **Cookie Auth Redirect Fix**: Access denied preserves tenant context
4. ✅ **Role Seeding Fix**: Creates `tenant-admin` role in `default` realm

All four pieces working together = tenant admin can now access admin pages!

### Files Modified

- `MrWhoOidc.WebAuth/Services/TenantSeedingService.cs`:
  - Create `tenant-admin` role in `default` realm
  - Assign `tenant-admin` role to admin user in `default` realm
  - Keep legacy `admin` role in `admin` realm for compatibility

### Related Files

- Authorization: `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthorizationHandler.cs`
- Configuration: `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthOptions.cs`
- Policy Registration: `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/AuthenticationAuthorizationExtensions.cs`

### Next Steps

After reseeding tenants with the fixed seeder:
- Tenant admins will have proper access to all admin pages
- Authorization will work correctly with tenant context
- No more "Access Denied" errors for properly configured tenant admins

For existing tenants (before the fix), you can manually add the role:
```sql
-- Get tenant, realm, user IDs
SELECT t.Id as TenantId, rl.Id as DefaultRealmId, u.Id as UserId
FROM Tenants t
JOIN Realms rl ON rl.TenantId = t.Id AND rl.Name = 'default'
JOIN Users u ON u.TenantId = t.Id
WHERE t.Slug = 'pop-app' AND u.Email = 'admin@pop.app';

-- Create tenant-admin role in default realm
INSERT INTO Roles (Id, Name, RealmId, TenantId, IsActive)
VALUES (gen_random_uuid(), 'tenant-admin', {DefaultRealmId}, {TenantId}, true);

-- Get the role ID
SELECT Id FROM Roles WHERE Name = 'tenant-admin' AND RealmId = {DefaultRealmId};

-- Assign role to user
INSERT INTO UserRoleAssignments (Id, UserId, RoleId, ClientId, RealmId, IsActive)
VALUES (gen_random_uuid(), {UserId}, {RoleId}, {ClientId}, {DefaultRealmId}, true);
```
