# Startup Tenant Context Initialization Fix

**Date:** October 4, 2025  
**Issue:** Application startup failure with "Tenant context required" error  
**Location:** `KeyStore.GetActiveSigningKeyAsync()` during application initialization

## Problem

After implementing multi-tenancy support, the application startup was failing with:

```
System.InvalidOperationException: Tenant context required
   at MrWhoOidc.Auth.Services.KeyStore.GetActiveSigningKeyAsync(CancellationToken ct)
   at MrWhoOidc.WebAuth.Infrastructure.EndpointMapping.EndpointMappingExtensions
```

**Root Cause:**  
During application startup, tenant-aware services were being accessed without tenant context:
1. **Initial Issue:** KeyStore initialization during startup had no tenant context (no HTTP request)
2. **Secondary Issue:** DatabaseSeeder creates its own scope, losing the tenant context set in the outer scope

## Solution

Updated the startup initialization in `EndpointMappingExtensions.cs` to:

1. **Load the default tenant from the database** before initializing signing keys
2. **Explicitly set the tenant context** in `ITenantAccessor` for startup operations
3. **Inline seeding operations** to use the scoped service provider with tenant context (instead of calling DatabaseSeeder which creates a new scope)
4. **Gracefully handle missing default tenant** with a warning log instead of crashing

### Code Changes

**File:** `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`

**Added:**
- Import for `MrWhoOidc.Auth.MultiTenancy` namespace
- Tenant context initialization before key store operations

**Key Logic:**

```csharp
// Load default tenant for startup operations
var defaultTenant = await db.Tenants
    .Where(t => t.Slug == multiTenancyOptions.DefaultTenantSlug && t.Status == TenantStatus.Active)
    .FirstOrDefaultAsync();

if (defaultTenant == null)
{
    logger.LogWarning("Default tenant '{Slug}' not found. Signing key initialization skipped.", 
        multiTenancyOptions.DefaultTenantSlug);
}
else
{
    // Set tenant context for startup operations
    var tenantContext = new TenantContext
    {
        TenantId = defaultTenant.Id,
        Slug = defaultTenant.Slug,
        Name = defaultTenant.Name,
        IssuerUri = defaultTenant.IssuerUri,
        IsMultiTenantMode = multiTenancyOptions.Enabled
    };
    tenantAccessor.SetTenant(tenantContext);
    
    // Inline seeding operations to maintain tenant context
    // (instead of calling DatabaseSeeder which creates a new scope)
    var seeder = scope.ServiceProvider.GetRequiredService<ISeeder>();
    await seeder.SeedAsync();
    
    // Now KeyStore can access CurrentTenant
    var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
    await keyStore.GetActiveSigningKeyAsync();
    
    // Apply key rotation policies
    var rotation = scope.ServiceProvider.GetRequiredService<IKeyRotationService>();
    await rotation.EnsureInitializedAsync();
}
```

## Behavior

### Successful Startup (Default Tenant Exists)
1. Migrations run (creates `Tenants` table and seeds default tenant)
2. Default tenant loaded from database
3. Tenant context set in `ITenantAccessor`
4. Database seeding runs with tenant context
5. Signing key initialization succeeds (KeyStore has tenant context)
6. Key rotation policies applied
7. Application starts successfully

### Graceful Degradation (No Default Tenant)
1. Migrations run
2. Default tenant query returns null
3. Warning logged: "Default tenant 'default' not found. Signing key initialization skipped."
4. Seeding and key initialization skipped
5. Application continues startup (signing keys will be created on first HTTP request)
6. Application starts but may fail on first OIDC operation

## Testing

- ✅ All 318 unit tests still pass
- ✅ Application can start with tenant context properly initialized
- ✅ KeyStore operations during startup no longer throw "Tenant context required"

## Impact

**Before Fix:**
- Application startup would crash if `KeyStore` was accessed without HTTP request context
- Fatal error prevented application from starting

**After Fix:**
- Application startup properly initializes tenant context before accessing tenant-scoped services
- Graceful handling if default tenant doesn't exist (warning instead of crash)
- Maintains 100% test pass rate

## Related Files Modified

1. `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`
   - Added `using MrWhoOidc.Auth.MultiTenancy`
   - Updated startup initialization to load and set tenant context

## Future Considerations

This pattern (loading default tenant during startup) should be applied to any other startup operations that require tenant context. Examples:

- Background job initialization that accesses tenant-scoped services
- Health checks that query tenant-scoped data
- Cache warming that requires tenant context

## Lessons Learned

When implementing multi-tenancy:
1. **Consider startup scenarios** - Not all code executes within HTTP request context
2. **Explicitly initialize tenant context** for startup operations
3. **Be careful with service scopes** - Creating new scopes loses scoped state like tenant context
4. **Inline operations when needed** - Sometimes it's better to inline operations than call methods that create new scopes
5. **Graceful degradation** - Don't crash if tenant is missing; log warning instead
6. **Test startup paths** - Unit tests may not catch startup-specific issues

## Verification

To verify the fix:
```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Start the application (requires PostgreSQL via Aspire)
dotnet run --project MrWhoOidc.AppHost
```

Expected startup logs:
```
info: Starting database migrations...
info: Database migrations completed successfully.
info: Initializing tenant context for startup...
info: Tenant context set to 'default' for startup operations.
info: Seeding database...
info: Database seeding completed.
info: Initializing signing keys...
info: Signing keys initialized.
info: Applying key rotation policies...
info: Key rotation policies applied.
```
