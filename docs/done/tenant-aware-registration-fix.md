# Tenant-Aware Registration Fix

**Date**: October 12, 2025  
**Issue**: Foreign key constraint violation when creating registrations

## Problem

The public registration page (`/Registrations/Index`) was failing with a PostgreSQL foreign key constraint error:

```
PostgresException: 23503: insert or update on table "Registrations" violates foreign key constraint "FK_Registrations_Tenants_TenantId"
```

The `Registration` entity has a required `TenantId` foreign key, but the registration creation code was not setting it.

## Root Cause

The `Registration` entity was added to the database without setting its `TenantId` property, causing a foreign key constraint violation when trying to save to the database.

## Solution

Updated `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs` to be tenant-aware:

### 1. Injected `ITenantAccessor`

```csharp
public class IndexModel(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    ITenantAccessor tenantAccessor) : PageModel
```

### 2. Set `TenantId` on Registration

```csharp
var currentTenant = tenantAccessor.CurrentTenant;
if (currentTenant == null)
{
    ModelState.AddModelError(string.Empty, "Unable to determine tenant context.");
    return Page();
}

var entity = new Registration
{
    TenantId = currentTenant.TenantId,  // ✅ Now set correctly
    Email = email,
    // ... other properties
};
```

### 3. Made User Existence Check Tenant-Scoped

Before:
```csharp
var userExists = await db.Users.AsNoTracking()
    .AnyAsync(u => u.NormalizedEmail == normalized);
```

After:
```csharp
var userExists = await db.Users.AsNoTracking()
    .AnyAsync(u => u.TenantId == currentTenant.TenantId && u.NormalizedEmail == normalized);
```

This prevents false positives when the same email exists in a different tenant.

### 4. Made Pending Registration Check Tenant-Scoped

Before:
```csharp
var pending = await db.Set<Registration>().AsNoTracking()
    .FirstOrDefaultAsync(r => r.NormalizedEmail == normalized && r.State == "pending");
```

After:
```csharp
var pending = await db.Set<Registration>().AsNoTracking()
    .FirstOrDefaultAsync(r => 
        r.TenantId == currentTenant.TenantId && 
        r.NormalizedEmail == normalized && 
        r.State == "pending");
```

### 5. Made Client Loading Tenant-Scoped

Before:
```csharp
var clients = await db.Clients.AsNoTracking()
    .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
    .OrderBy(x => x.ClientId).ToListAsync();
```

After:
```csharp
var clients = await db.Clients.AsNoTracking()
    .Where(c => c.TenantId == currentTenant.TenantId)
    .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
    .OrderBy(x => x.ClientId).ToListAsync();
```

This ensures the client dropdown only shows clients from the current tenant.

## Testing

After the fix:
- ✅ Registrations can be created successfully
- ✅ Users see only clients from their tenant
- ✅ Email uniqueness is checked per-tenant
- ✅ Pending registration detection is per-tenant

## Related Files

- `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (Registration entity definition)

## Multi-Tenancy Pattern

This fix follows the standard multi-tenancy pattern used throughout the codebase:

1. Inject `ITenantAccessor` to get current tenant context
2. Check `CurrentTenant` is not null (defensive programming)
3. Filter all database queries by `TenantId`
4. Set `TenantId` on all new entities before saving

This pattern ensures complete data isolation between tenants.
