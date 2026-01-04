# TenantResolutionMiddleware HybridCache Integration - Quick Summary

## What Was Done

Added HybridCache to `TenantResolutionMiddleware.cs` (lines 66-89) to cache user-to-tenant slug lookups.

## The Problem

When an authenticated user hits a 404 (tenant not found), the middleware queries the database to find the user's tenant slug for redirect purposes. This happened on **every 404**, causing unnecessary DB load.

## The Solution

```csharp
// Cache the user→tenant slug mapping for 2 minutes
var userTenantSlug = await cache.GetOrCreateAsync(
    $"user:tenant:slug:{userId}",
    async cancel => { /* DB query */ },
    new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromMinutes(2)
    },
    tags: new[] { "user-tenant-mapping", $"user:{userId}" },
    cancellationToken: context.RequestAborted
);
```

## Performance Improvements

- ✅ **Eliminates repeated DB queries** for same user within 2 minutes
- ✅ **Stampede protection** - Multiple concurrent 404s only query once
- ✅ **Microsecond access times** from L1 (memory) cache
- ✅ **Multi-instance support** via L2 (Redis) cache when configured

## Cache Details

- **Key**: `user:tenant:slug:{userId}`
- **TTL**: 2 minutes (L1 and L2)
- **Tags**: `user-tenant-mapping`, `user:{userId}`

## When to Invalidate

Invalidate the cache when:

1. **User's tenant changes**: `await cache.RemoveAsync($"user:tenant:slug:{userId}")`
2. **Tenant slug changes**: `await cache.RemoveByTagAsync("user-tenant-mapping")`
3. **User deleted**: `await cache.RemoveByTagAsync($"user:{userId}")`

## TODO

⚠️ Add cache invalidation calls to:
- User admin pages (when changing user's tenant)
- Tenant admin pages (when changing tenant slug)
- User deletion logic

## Related Docs

- [Detailed Implementation Guide](./tenant-resolution-middleware-caching.md)
- [HybridCache Guide](./hybrid-cache-guide.md)
