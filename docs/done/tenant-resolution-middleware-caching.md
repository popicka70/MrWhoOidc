# TenantResolutionMiddleware HybridCache Implementation

## Summary

The `TenantResolutionMiddleware` now uses HybridCache to cache user-to-tenant slug mappings on lines 66-89. This significantly improves performance for 404 redirect scenarios where authenticated users need to be redirected to their tenant-specific NotFound page.

## What Changed

### Before
```csharp
// Direct database query on every 404 redirect
var dbContext = context.RequestServices.GetRequiredService<AuthDbContext>();
var userTenant = await (from u in dbContext.Users
                        join t in dbContext.Tenants on u.TenantId equals t.Id
                        where u.Id.ToString() == userId
                        select new { t.Slug })
    .FirstOrDefaultAsync(context.RequestAborted);
```

### After
```csharp
// Cached with HybridCache for 2 minutes
var userTenantSlug = await cache.GetOrCreateAsync(
    $"user:tenant:slug:{userId}",
    async cancel =>
    {
        var dbContext = context.RequestServices.GetRequiredService<AuthDbContext>();
        var result = await (from u in dbContext.Users
                            join t in dbContext.Tenants on u.TenantId equals t.Id
                            where u.Id.ToString() == userId
                            select t.Slug)
            .FirstOrDefaultAsync(cancel);
        return result; // Can be null
    },
    new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromMinutes(2)
    },
    tags: new[] { "user-tenant-mapping", $"user:{userId}" },
    cancellationToken: context.RequestAborted
);
```

## Performance Benefits

1. **Eliminates repeated DB queries**: Same user hitting multiple 404s only queries once per 2 minutes
2. **Stampede protection**: Multiple concurrent 404s for same user only execute query once
3. **Low latency**: L1 (memory) cache provides microsecond access times
4. **Multi-instance support**: L2 (Redis) cache shares mappings across instances when Redis is configured

## Cache Configuration

- **Cache Key Pattern**: `user:tenant:slug:{userId}`
- **Expiration**: 2 minutes (both L1 and L2)
- **Tags**: 
  - `user-tenant-mapping` - For bulk invalidation of all user-tenant mappings
  - `user:{userId}` - For invalidating a specific user's mapping

## Cache Invalidation

Cache entries should be invalidated when:

1. **User changes tenant** (tenant assignment updated)
2. **Tenant slug changes** (rare but possible)
3. **User is deleted**

### How to Invalidate

#### Option 1: Invalidate Specific User
```csharp
// When user's tenant assignment changes
await cache.RemoveAsync($"user:tenant:slug:{userId}");

// Or using tags
await cache.RemoveByTagAsync($"user:{userId}");
```

#### Option 2: Invalidate All User-Tenant Mappings
```csharp
// When tenant slug changes (affects all users in that tenant)
await cache.RemoveByTagAsync("user-tenant-mapping");
```

### Implementation Locations

Cache invalidation should be added to:

1. **User Administration** (when updating user's tenant):
   - `MrWhoOidc.WebAuth/Pages/Admin/Users/*` - Admin UI pages
   - Any API endpoints that modify user tenant assignments

2. **Tenant Administration** (when changing tenant slug):
   - `MrWhoOidc.WebAuth/Pages/Admin/Tenants/*` - Tenant management pages

3. **User Deletion**:
   - Wherever user accounts are deleted

### Example Implementation

```csharp
// In a service that updates user tenant assignment
public async Task UpdateUserTenantAsync(Guid userId, Guid newTenantId, HybridCache cache, CancellationToken ct = default)
{
    // Update the database
    var user = await _dbContext.Users.FindAsync(userId);
    user.TenantId = newTenantId;
    await _dbContext.SaveChangesAsync(ct);
    
    // Invalidate the cache
    await cache.RemoveAsync($"user:tenant:slug:{userId}", ct);
}

// In a service that updates tenant slug
public async Task UpdateTenantSlugAsync(Guid tenantId, string newSlug, HybridCache cache, CancellationToken ct = default)
{
    // Update the database
    var tenant = await _dbContext.Tenants.FindAsync(tenantId);
    tenant.Slug = newSlug;
    await _dbContext.SaveChangesAsync(ct);
    
    // Invalidate all user-tenant mappings (slug changed affects all users)
    await cache.RemoveByTagAsync("user-tenant-mapping", ct);
}
```

## Testing

The cache works seamlessly in tests without Redis (L1-only mode). No special test configuration needed.

## Monitoring Recommendations

Consider adding metrics to track:
1. Cache hit/miss ratio for user-tenant lookups
2. Query execution time (should drop significantly)
3. 404 redirect response times

## Related Documentation

- [HybridCache Guide](./hybrid-cache-guide.md) - Comprehensive usage guide
- [HybridCache Setup Complete](./hybrid-cache-setup-complete.md) - Setup summary

## Status

✅ **Implemented**: HybridCache integrated into TenantResolutionMiddleware  
⚠️ **TODO**: Add cache invalidation to user/tenant admin pages  
📋 **Optional**: Add metrics/monitoring for cache effectiveness
