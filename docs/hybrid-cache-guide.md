# HybridCache Guide

## Overview

HybridCache is a modern .NET caching feature that provides a unified caching API combining:
- **L1 (Local/Memory) Cache**: Fast in-memory caching for single-instance scenarios
- **L2 (Distributed) Cache**: Optional Redis-backed distributed cache for multi-instance scenarios
- **Stampede Protection**: Automatic coordination to prevent cache stampedes
- **Serialization**: Uses `System.Text.Json` by default (custom serializers can be registered)

## Setup

HybridCache is configured in `Program.cs` via the `AddMrWhoOidcHybridCache` extension:

```csharp
// Redis (optional distributed features)
var redisMux = builder.Services.AddMrWhoOidcRedis(builder.Configuration);

// HybridCache (L1 + optional L2 via Redis)
builder.Services.AddMrWhoOidcHybridCache(builder.Configuration, redisMux);
```

### Configuration

Add to `appsettings.json` (optional):

```json
{
  "HybridCache": {
    "MaximumPayloadMB": 1,
    "DefaultExpirationMinutes": 5
  },
  "ConnectionStrings": {
    "redis": "localhost:6379"  // Optional: enables L2 distributed cache
  }
}
```

Built-in limits: 1 MB per entry (`MaximumPayloadMB` overrides), 512-character keys, and a default expiration of 5 minutes for both tiers (`DefaultExpirationMinutes` overrides both).

With `ConnectionStrings:redis` set, Redis is registered as the L2 tier with the key prefix `mrwhooidc:`. Without it, HybridCache runs L1-only (memory-only), which is fine for single-instance deployments; in multi-replica deployments L1-only means each replica has its own cache and invalidations do not propagate.

**Security:** with Redis configured, every entry is serialized into Redis unless the call opts out. Never cache secrets or private key material in the distributed tier: use `HybridCacheEntryFlags.DisableDistributedCache`, as `KeyStore` does for the active private signing/encryption JWKs.

## Usage

### Basic Usage

Inject `HybridCache` into your service:

```csharp
using Microsoft.Extensions.Caching.Hybrid;

public class MyService
{
    private readonly HybridCache _cache;
    
    public MyService(HybridCache cache)
    {
        _cache = cache;
    }
    
    public async Task<string> GetDataAsync(string key, CancellationToken ct = default)
    {
        // Get or create cache entry with factory
        return await _cache.GetOrCreateAsync(
            key,
            async cancel => await FetchDataFromSourceAsync(key, cancel),
            cancellationToken: ct
        );
    }
    
    private async Task<string> FetchDataFromSourceAsync(string key, CancellationToken ct)
    {
        // Expensive operation (DB query, API call, etc.)
        await Task.Delay(100, ct);
        return $"Data for {key}";
    }
}
```

### With Custom Expiration

```csharp
public async Task<UserProfile> GetUserProfileAsync(string userId, CancellationToken ct = default)
{
    var options = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(10),          // L2 (Redis) expiration
        LocalCacheExpiration = TimeSpan.FromMinutes(2)  // L1 (memory) expiration
    };
    
    return await _cache.GetOrCreateAsync(
        $"user:profile:{userId}",
        async cancel => await _userRepository.GetByIdAsync(userId, cancel),
        options,
        cancellationToken: ct
    );
}
```

### Removing Cache Entries

```csharp
public async Task InvalidateUserCacheAsync(string userId, CancellationToken ct = default)
{
    await _cache.RemoveAsync($"user:profile:{userId}", ct);
}
```

### Tags for Bulk Invalidation

```csharp
public async Task<List<Client>> GetClientsByTenantAsync(string tenantId, CancellationToken ct = default)
{
    var options = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };
    
    // Tags are passed as a separate parameter for bulk invalidation
    var tags = new List<string> { $"tenant:{tenantId}", "clients" };
    
    return await _cache.GetOrCreateAsync(
        $"clients:tenant:{tenantId}",
        async cancel => await _clientRepository.GetByTenantAsync(tenantId, cancel),
        options,
        tags,
        cancellationToken: ct
    );
}

// Invalidate all cache entries for a tenant
public async Task InvalidateTenantCacheAsync(string tenantId, CancellationToken ct = default)
{
    await _cache.RemoveByTagAsync($"tenant:{tenantId}", ct);
}
```

## Migration from IMemoryCache

### Before (IMemoryCache)

```csharp
public class OldService
{
    private readonly IMemoryCache _cache;
    
    public OldService(IMemoryCache cache)
    {
        _cache = cache;
    }
    
    public async Task<string> GetDataAsync(string key)
    {
        if (_cache.TryGetValue(key, out string? cached))
        {
            return cached!;
        }
        
        var data = await FetchDataAsync(key);
        
        _cache.Set(key, data, TimeSpan.FromMinutes(5));
        
        return data;
    }
}
```

### After (HybridCache)

```csharp
public class NewService
{
    private readonly HybridCache _cache;
    
    public NewService(HybridCache cache)
    {
        _cache = cache;
    }
    
    public async Task<string> GetDataAsync(string key, CancellationToken ct = default)
    {
        // Single call with stampede protection built-in
        return await _cache.GetOrCreateAsync(
            key,
            async cancel => await FetchDataAsync(key),
            new HybridCacheEntryOptions 
            { 
                Expiration = TimeSpan.FromMinutes(5) 
            },
            cancellationToken: ct
        );
    }
}
```

## Benefits Over IMemoryCache

1. **Stampede Protection**: Built-in coordination prevents multiple concurrent requests from executing the same expensive operation
2. **Distributed Support**: Uses Redis as an L2 cache when a connection is configured
3. **Simpler API**: Single `GetOrCreateAsync` call vs. manual TryGetValue/Set logic
4. **Serialization**: Uses `System.Text.Json` by default
5. **Type Safety**: Strongly-typed entries with compile-time safety
6. **Cancellation**: First-class `CancellationToken` support
7. **Tags**: Built-in tagging for bulk invalidation scenarios

## Current Usage in MrWhoOidc

HybridCache is injected by, among others: `ClientStore` (client lookups), `TenantService` and `TenantResolutionMiddleware` (tenant resolution), `TenantSettingsService`, `PlatformSettingsService`, `UserService`, `PlatformInitialAccessTokenService`, `WebAuthnService` (challenge sessions), `PublicJwksCache` (published JWKS) and `KeyStore` (active keys, memory-only). `CorrelationStateCache` still uses `IMemoryCache` plus optional direct Redis. `MrWhoOidc.WebAuth/Examples/HybridCacheExampleService.cs` is a reference sample and is not registered.

## Performance Considerations

- **L1 (Memory)**: Microsecond access times, but per-instance (not shared)
- **L2 (Redis)**: Millisecond access times, shared across instances
- **Factory Execution**: Only one concurrent execution per key (stampede protection)
- **Serialization**: Uses `System.Text.Json` by default; ensure types are serializable

## Best Practices

1. **Use short L1 expiration** for data that changes frequently
2. **Use longer L2 expiration** for data that's expensive to compute but rarely changes
3. **Use tags** for related cache entries that need bulk invalidation
4. **Always pass CancellationToken** for proper request cancellation
5. **Key naming**: Use consistent, structured key patterns (e.g., `resource:identifier:subresource`)
6. **Keep sensitive data out of L2**: use `HybridCacheEntryFlags.DisableDistributedCache` for anything that must not reach Redis
7. **Monitor cache hit rates** and adjust expiration times based on metrics

## Testing

HybridCache works in unit tests without Redis:

```csharp
[TestMethod]
public async Task TestWithHybridCache()
{
    var services = new ServiceCollection();
    services.AddHybridCache(); // L1-only mode
    
    var sp = services.BuildServiceProvider();
    var cache = sp.GetRequiredService<HybridCache>();
    
    // Use cache in tests
    var result = await cache.GetOrCreateAsync(
        "test-key",
        async _ => await Task.FromResult("test-value")
    );
    
    Assert.AreEqual("test-value", result);
}
```

## Related Documentation

- [Microsoft Learn: HybridCache](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
- [Developer guide](./developer-guide.md) (local Redis setup)
