# Phase 3: Platform Admin Tenant Creation Page Fix

## Problem: Missing ClientId in UserRoleAssignment

### Discovery
After fixing `TenantSeedingService.cs`, the same error still occurred when creating tenants through the Platform Admin UI. The stack trace showed:

```
InvalidOperationException: The value of 'UserRoleAssignment.ClientId' is unknown when attempting to save changes.
...
at MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants.CreateModel.OnPostAsync() in Create.cshtml.cs
```

### Root Cause

The `Pages/PlatformAdmin/Tenants/Create.cshtml.cs` page had **two issues**:

1. **Missing ClientId**: `UserRoleAssignment` creation was missing the required `ClientId` property
2. **Missing tenant-admin Role**: Only created `admin` role in `admin` realm, not `tenant-admin` in `default` realm

### Code Before (Broken)

```csharp
// Create admin role in admin realm
var adminRole = new Role
{
    Name = "admin",
    RealmId = adminRealm.Id,
    TenantId = tenant.Id,
    IsActive = true
};
db.Roles.Add(adminRole);
await db.SaveChangesAsync();

// Create admin user
var adminUser = new User { ... };
db.Users.Add(adminUser);
await db.SaveChangesAsync();

// Assign admin role to user (BROKEN!)
var roleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = adminRole.Id,
    RealmId = adminRealm.Id,  // ← Missing ClientId!
    IsActive = true
};
db.UserRoleAssignments.Add(roleAssignment);
await db.SaveChangesAsync();  // ← EXCEPTION!
```

### Why It Failed

`UserRoleAssignment` has a **composite primary key** that includes `ClientId`:

```csharp
// From AuthDbContext.cs
modelBuilder.Entity<UserRoleAssignment>(b =>
{
    b.HasKey(x => new { x.UserId, x.RoleId, x.ClientId, x.RealmId });
    // ... foreign key relationships
});
```

All four properties (UserId, RoleId, ClientId, RealmId) are **required** because they form the composite key.

## The Fix

### Part 1: Create tenant-admin Role in default Realm

```csharp
// Create tenant-admin role in default realm (for tenant admin authorization)
var tenantAdminRole = new Role
{
    Name = "tenant-admin",
    RealmId = defaultRealm.Id,  // ← default realm
    TenantId = tenant.Id,
    IsActive = true
};
db.Roles.Add(tenantAdminRole);

// Create admin role in admin realm (legacy)
var adminRole = new Role
{
    Name = "admin",
    RealmId = adminRealm.Id,  // ← admin realm
    TenantId = tenant.Id,
    IsActive = true
};
db.Roles.Add(adminRole);
await db.SaveChangesAsync();
```

### Part 2: Create Client Before Role Assignment

```csharp
// Create admin user
var adminUser = new User
{
    TenantId = tenant.Id,
    Username = Input.AdminEmail.Split('@')[0],
    Email = Input.AdminEmail,
    NormalizedEmail = normalizedEmail,
    Name = "Admin User",
    PasswordHash = passwordHash,
    HashAlgorithm = "argon2id",
    EmailVerified = true,
    EmailVerifiedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow
};
db.Users.Add(adminUser);
await db.SaveChangesAsync();

// Create admin client for role assignments
var adminClient = new Client
{
    ClientId = $"{Input.Slug}-admin",
    ClientName = $"{Input.Name} Admin Portal",
    TenantId = tenant.Id,
    RealmId = adminRealm.Id,
    RequirePkce = true,
    RequireConsent = false,
    AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        $"{baseUrl}/t/{Input.Slug}/signin-oidc"
    }),
    AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        $"{baseUrl}/t/{Input.Slug}/signout-callback-oidc",
        $"{baseUrl}/t/{Input.Slug}/"
    })
};
db.Clients.Add(adminClient);
await db.SaveChangesAsync();  // ← Client saved, ID generated

// Assign tenant-admin role to user (in default realm)
var tenantAdminRoleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = tenantAdminRole.Id,
    ClientId = adminClient.Id,  // ← Now includes ClientId!
    RealmId = defaultRealm.Id,
    IsActive = true
};
db.UserRoleAssignments.Add(tenantAdminRoleAssignment);

// Assign admin role to user (in admin realm, legacy)
var roleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = adminRole.Id,
    ClientId = adminClient.Id,  // ← Includes ClientId!
    RealmId = adminRealm.Id,
    IsActive = true
};
db.UserRoleAssignments.Add(roleAssignment);
await db.SaveChangesAsync();  // ← Success! ✅
```

## Key Changes

1. **Create Both Roles**: `tenant-admin` in `default` realm + `admin` in `admin` realm
2. **Create Client**: Before role assignments, create admin client with redirect URIs
3. **Include ClientId**: All `UserRoleAssignment` records include `ClientId = adminClient.Id`
4. **Assign Both Roles**: Admin user gets both `tenant-admin` (for authorization) and `admin` (legacy)

## Files Modified

- **MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Create.cshtml.cs**:
  - Added `tenant-admin` role creation in `default` realm
  - Added admin client creation with redirect URIs
  - Fixed `UserRoleAssignment` to include `ClientId`
  - Assign both roles to admin user

## Testing

### Before Fix
```
1. Go to: https://localhost:8443/PlatformAdmin/Tenants/Create
2. Fill form:
   - Slug: test-tenant
   - Name: Test Tenant
   - Admin Email: admin@test.local
   - Admin Password: Admin123!
3. Click "Create"
4. Result: InvalidOperationException ❌
```

### After Fix
```
1. Restart: docker compose down -v && docker compose up -d --build
2. Go to: https://localhost:8443/PlatformAdmin/Tenants/Create
3. Fill form:
   - Slug: pop-app
   - Name: Pop App
   - Admin Email: admin@pop.app
   - Admin Password: Admin123!
4. Click "Create"
5. Result: SUCCESS ✅
6. Tenant created with:
   - default realm + tenant-admin role
   - admin realm + admin role
   - pop-app-admin client
   - admin@pop.app user with both roles
```

### Verification Query

```sql
SELECT 
    t.Slug as TenantSlug,
    u.Email as UserEmail,
    c.ClientId,
    rl.Name as RealmName,
    r.Name as RoleName,
    ura.IsActive
FROM UserRoleAssignments ura
JOIN Users u ON ura.UserId = u.Id
JOIN Clients c ON ura.ClientId = c.Id
JOIN Roles r ON ura.RoleId = r.Id
JOIN Realms rl ON r.RealmId = rl.Id
JOIN Tenants t ON rl.TenantId = t.Id
WHERE t.Slug = 'pop-app';
```

**Expected Result**:
| TenantSlug | UserEmail | ClientId | RealmName | RoleName | IsActive |
|------------|-----------|----------|-----------|----------|----------|
| pop-app | admin@pop.app | pop-app-admin | default | tenant-admin | true |
| pop-app | admin@pop.app | pop-app-admin | admin | admin | true |

## Related Fixes

This completes the tenant creation fix chain:

1. ✅ **TenantSeedingService**: Fixed batch client creation (used by API/old Platform Admin)
2. ✅ **Platform Admin Create Page**: Fixed client + role creation (used by UI)

Both paths now:
- Create `tenant-admin` role in `default` realm
- Create admin client with redirect URIs
- Include `ClientId` in all `UserRoleAssignment` records
- Assign both `tenant-admin` and `admin` roles to admin user

## UserRoleAssignment Schema

For reference, the complete `UserRoleAssignment` structure:

```csharp
public class UserRoleAssignment
{
    public Guid UserId { get; set; }      // ← Part of composite PK
    public Guid RoleId { get; set; }      // ← Part of composite PK
    public Guid ClientId { get; set; }    // ← Part of composite PK (was missing!)
    public Guid RealmId { get; set; }     // ← Part of composite PK
    public bool IsActive { get; set; } = true;
}
```

**All four GUID properties are required** - they form the composite primary key and foreign key relationships.

## Login Flow After Fix

```
1. User goes to: https://localhost:8443/DiscoverTenant
2. Enters: admin@pop.app
3. Redirected to: https://localhost:8443/t/pop-app/login
4. Login successful ✅
5. Redirected to: https://localhost:8443/t/pop-app/
6. Click "Clients" menu
7. URL: https://localhost:8443/t/pop-app/Admin/Clients
8. Authorization check:
   - User: admin@pop.app
   - Tenant: pop-app
   - Role: tenant-admin
   - Realm: default
   - Result: AUTHORIZED ✅
9. Page loads successfully! 🎉
```

## Summary

The Platform Admin tenant creation page was missing:
1. Creation of `tenant-admin` role in `default` realm
2. Creation of admin client before role assignment
3. `ClientId` property in `UserRoleAssignment` records

The fix ensures both the seeding service and the UI create consistent tenant structures with proper role assignments that pass authorization checks.
