# Phase 3: Tenant Seeding EF Core Foreign Key Fix

## Problem: UserRoleAssignment Foreign Key Error

### Error Message
```
System.InvalidOperationException: The value of 'UserRoleAssignment.ClientId' is unknown when attempting to save changes. 
This is because the property is also part of a foreign key for which the principal entity in the relationship is not known.
```

### Root Cause

The `TenantSeedingService.SeedSampleTenantAsync` was saving clients one at a time, then immediately trying to create `UserRoleAssignment` records. The issue was:

1. **Sequential Saves**: Saved `adminClient`, then saved `webClient` separately
2. **EF Core Tracking Issues**: After the first `SaveChangesAsync`, EF Core might not properly track the relationship
3. **Foreign Key Resolution**: When adding `UserRoleAssignment` with `ClientId = adminClient.Id`, EF Core couldn't resolve the foreign key relationship

### Code Before (Broken)

```csharp
// Create admin client
var adminClient = new Client { ... };
_db.Clients.Add(adminClient);
await _db.SaveChangesAsync(ct);  // ← First save

// Create web client
var webClient = new Client { ... };
_db.Clients.Add(webClient);
await _db.SaveChangesAsync(ct);  // ← Second save

// Create role assignments (FAILS!)
var roleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = tenantAdminRole.Id,
    ClientId = adminClient.Id,  // ← EF Core doesn't recognize this FK!
    RealmId = defaultRealm.Id,
    IsActive = true
};
_db.UserRoleAssignments.Add(roleAssignment);
await _db.SaveChangesAsync(ct);  // ← EXCEPTION!
```

### Why It Failed

`UserRoleAssignment` is configured as a join table with composite primary key and foreign keys to `User`, `Role`, `Client`, and `Realm`:

```csharp
// From AuthDbContext.cs OnModelCreating
modelBuilder.Entity<UserRoleAssignment>(b =>
{
    b.HasKey(x => new { x.UserId, x.RoleId, x.ClientId, x.RealmId });
    b.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
    b.HasOne<Role>()
        .WithMany()
        .HasForeignKey(x => x.RoleId)
        .OnDelete(DeleteBehavior.Cascade);
    b.HasOne<Client>()          // ← This relationship
        .WithMany()
        .HasForeignKey(x => x.ClientId)
        .OnDelete(DeleteBehavior.Cascade);
    b.HasOne<Realm>()
        .WithMany()
        .HasForeignKey(x => x.RealmId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

After calling `SaveChangesAsync` multiple times in sequence, EF Core's change tracker might lose the relationship context, causing it to not recognize `adminClient.Id` as a valid foreign key reference.

## The Fix

### Strategy: Batch Client Creation

Save **both clients together** before creating role assignments:

```csharp
// Create both clients
var adminClient = new Client
{
    ClientId = $"{tenantSlug}-admin",
    ClientName = $"{tenantName} Admin Portal",
    TenantId = tenant.Id,
    RealmId = adminRealm.Id,
    RequirePkce = true,
    RequireConsent = false,
    AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        $"https://localhost:8443/t/{tenantSlug}/signin-oidc",
        $"http://localhost:8443/t/{tenantSlug}/signin-oidc"
    }),
    AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        $"https://localhost:8443/t/{tenantSlug}/signout-callback-oidc",
        $"https://localhost:8443/t/{tenantSlug}/",
        $"http://localhost:8443/t/{tenantSlug}/signout-callback-oidc",
        $"http://localhost:8443/t/{tenantSlug}/"
    })
};

var webClient = new Client
{
    ClientId = $"{tenantSlug}-web",
    ClientName = $"{tenantName} Web Application",
    TenantId = tenant.Id,
    RealmId = defaultRealm.Id,
    RequirePkce = true,
    RequireConsent = false,
    AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        "https://localhost:5001/signin-oidc",
        "http://localhost:5001/signin-oidc"
    }),
    AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[] 
    { 
        "https://localhost:5001/signout-callback-oidc",
        "https://localhost:5001/",
        "http://localhost:5001/signout-callback-oidc",
        "http://localhost:5001/"
    })
};

// Add both clients to change tracker
_db.Clients.Add(adminClient);
_db.Clients.Add(webClient);

// Save both together (single transaction, proper FK tracking)
await _db.SaveChangesAsync(ct);

_logger.LogInformation("Created admin client {AdminClientId} and web client {WebClientId} for tenant {TenantSlug}", 
    adminClient.ClientId, webClient.ClientId, tenantSlug);

// Validate IDs were generated
if (adminClient.Id == Guid.Empty || webClient.Id == Guid.Empty)
{
    _logger.LogError("Client IDs not generated for tenant {TenantSlug}. Admin: {AdminId}, Web: {WebId}", 
        tenantSlug, adminClient.Id, webClient.Id);
    return TenantSeedResult.Failure("Failed to create clients - IDs not generated");
}

_logger.LogInformation("Client IDs confirmed: Admin={AdminId}, Web={WebId}", adminClient.Id, webClient.Id);

// NOW create role assignments (FK relationships properly tracked)
var tenantAdminRoleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = tenantAdminRole.Id,
    ClientId = adminClient.Id,  // ← EF Core recognizes this now!
    RealmId = defaultRealm.Id,
    IsActive = true
};
_db.UserRoleAssignments.Add(tenantAdminRoleAssignment);

var adminRoleAssignment = new UserRoleAssignment
{
    UserId = adminUser.Id,
    RoleId = adminRole.Id,
    ClientId = adminClient.Id,
    RealmId = adminRealm.Id,
    IsActive = true
};
_db.UserRoleAssignments.Add(adminRoleAssignment);

await _db.SaveChangesAsync(ct);
```

### Key Changes

1. **Batch Client Creation**: Add both clients to `_db.Clients` before calling `SaveChangesAsync`
2. **Single Save**: Only one `SaveChangesAsync` call for clients, keeping tracking context consistent
3. **ID Validation**: Added explicit check that GUIDs were generated
4. **Better Logging**: Log both client IDs together for debugging

### Why This Works

- **Single Transaction**: Both clients saved in same database transaction
- **Consistent Tracking**: EF Core maintains proper tracking of both entities
- **FK Resolution**: When creating `UserRoleAssignment`, EF Core can resolve `ClientId` foreign key because `adminClient` is still properly tracked
- **Proper Cascade**: All entities in the graph are properly related

## Files Modified

- **MrWhoOidc.WebAuth/Services/TenantSeedingService.cs**:
  - Combined client creation into single batch
  - Added ID validation
  - Improved logging

## Testing

### Before Fix
```powershell
# Create tenant via Platform Admin UI
# Result: System.InvalidOperationException with FK error
```

### After Fix
```powershell
# Restart with fresh database
docker compose down -v
docker compose up -d --build

# Create tenant via Platform Admin UI
# Go to: https://localhost:8443/PlatformAdmin
# Seed tenant: pop-app / "Pop App"
# Result: SUCCESS ✅
```

### Verification

Check database to confirm role assignments were created:

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
WHERE t.Slug = 'pop-app'
  AND u.Email LIKE '%@pop-app%';
```

**Expected output**:
| TenantSlug | UserEmail | ClientId | RealmName | RoleName | IsActive |
|------------|-----------|----------|-----------|----------|----------|
| pop-app | admin@pop-app.local | pop-app-admin | default | tenant-admin | true |
| pop-app | admin@pop-app.local | pop-app-admin | admin | admin | true |

## Related Issues

This fix complements the earlier fixes:
1. ✅ **Navigation Fix**: Layout uses tenant context
2. ✅ **Login Redirect Fix**: Preserves tenant path
3. ✅ **Cookie Auth Fix**: Access denied redirects preserve tenant
4. ✅ **Role Seeding Fix**: Creates `tenant-admin` in `default` realm
5. ✅ **EF Core FK Fix**: Batch client creation before role assignments

## Best Practices Learned

### EF Core Entity Tracking
- **Batch Related Entities**: When entities have FK relationships, add them all to context before saving
- **Single SaveChanges**: Minimize calls to `SaveChangesAsync` to keep tracking consistent
- **Validate IDs**: After save, check that auto-generated IDs are populated

### Join Table Relationships
- **Understand FK Configuration**: Know which properties are part of composite keys and FKs
- **Principal Entity Must Be Tracked**: Before creating dependent entity, ensure principal is in change tracker
- **Test With Fresh Context**: Integration tests should use fresh DbContext to catch tracking issues

### Debugging Tips
- **Enable SQL Logging**: Use `EnableSensitiveDataLogging()` in development
- **Check ChangeTracker.Entries()**: Inspect entity states before save
- **Use Explicit Loading**: If tracking is lost, use `_db.Entry(entity).Reload()` or query fresh

## Summary

The EF Core foreign key error was caused by sequential saves breaking entity tracking context. The fix batches client creation into a single save operation before creating dependent `UserRoleAssignment` records, ensuring EF Core properly resolves foreign key relationships.

With this fix, tenant seeding now works end-to-end:
1. Creates tenant
2. Creates realms (default, admin)
3. Creates roles (tenant-admin in default, admin in admin)
4. Creates admin user
5. Creates clients (admin-client, web-client) **← FIXED**
6. Assigns roles to user **← NOW WORKS**
7. Creates scopes
8. Returns success
