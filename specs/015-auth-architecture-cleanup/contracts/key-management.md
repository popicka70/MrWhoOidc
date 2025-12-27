# Key Management Contracts

**Feature**: 015-auth-architecture-cleanup  
**Domain**: Key Management Services

## Overview

This contract defines the cached key provider interface that eliminates the blocking async call in JwtService. The key insight from research is to cache keys with short TTL rather than making the entire JWT creation path async.

---

## ICachedKeyProvider

Provides cached access to cryptographic keys.

### Interface

```csharp
namespace MrWhoOidc.Auth.Services.KeyManagement;

/// <summary>
/// Provides cached access to signing and validation keys.
/// Eliminates blocking async calls in synchronous code paths.
/// </summary>
public interface ICachedKeyProvider
{
    /// <summary>
    /// Gets the current active signing key (cached).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The active signing key as JsonWebKey.</returns>
    Task<JsonWebKey> GetActiveSigningKeyAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets all keys valid for token validation (cached).
    /// Includes active key plus recently rotated keys still within grace period.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of validation keys.</returns>
    Task<IReadOnlyList<JsonWebKey>> GetValidationKeysAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Invalidates the cache, forcing reload on next access.
    /// Called after key rotation events.
    /// </summary>
    void InvalidateCache();
    
    /// <summary>
    /// Preloads the cache. Called during application startup.
    /// </summary>
    Task WarmupAsync(CancellationToken ct = default);
}
```

---

## Implementation Pattern

From [research.md](../research.md#r1-async-key-loading-pattern-for-jwtservice):

```csharp
namespace MrWhoOidc.Auth.Services.KeyManagement;

internal sealed class CachedKeyProvider(
    IKeyStore keyStore,
    ILogger<CachedKeyProvider> logger,
    TimeProvider timeProvider) : ICachedKeyProvider
{
    private JsonWebKey? _cachedSigningKey;
    private IReadOnlyList<JsonWebKey>? _cachedValidationKeys;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<JsonWebKey> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        if (_cachedSigningKey != null && timeProvider.GetUtcNow() < _cacheExpiry)
            return _cachedSigningKey;
        
        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedSigningKey != null && timeProvider.GetUtcNow() < _cacheExpiry)
                return _cachedSigningKey;
            
            logger.LogDebug("Refreshing signing key cache");
            var jwk = await keyStore.GetActiveSigningKeyAsync(ct);
            _cachedSigningKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
            _cacheExpiry = timeProvider.GetUtcNow().Add(CacheDuration);
            
            return _cachedSigningKey;
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public async Task<IReadOnlyList<JsonWebKey>> GetValidationKeysAsync(CancellationToken ct = default)
    {
        if (_cachedValidationKeys != null && timeProvider.GetUtcNow() < _cacheExpiry)
            return _cachedValidationKeys;
        
        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedValidationKeys != null && timeProvider.GetUtcNow() < _cacheExpiry)
                return _cachedValidationKeys;
            
            logger.LogDebug("Refreshing validation key cache");
            var keys = await keyStore.GetValidationKeysAsync(ct);
            _cachedValidationKeys = keys
                .Select(k => new JsonWebKey(k.ToJson()))
                .ToList()
                .AsReadOnly();
            _cacheExpiry = timeProvider.GetUtcNow().Add(CacheDuration);
            
            return _cachedValidationKeys;
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public void InvalidateCache()
    {
        logger.LogInformation("Key cache invalidated");
        _cachedSigningKey = null;
        _cachedValidationKeys = null;
        _cacheExpiry = DateTimeOffset.MinValue;
    }
    
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Warming up key cache");
        await GetActiveSigningKeyAsync(ct);
        await GetValidationKeysAsync(ct);
    }
}
```

---

## Integration with JwtService

The updated JwtService uses the cached provider:

```csharp
public class JwtService(
    ICachedKeyProvider keyProvider,  // Changed from IKeyStore
    IOptions<OidcOptions> options,
    TimeProvider timeProvider) : IJwtService
{
    public string CreateJwt(JwtPayload payload)
    {
        // Now this is safe because key is cached
        var signingKey = keyProvider.GetActiveSigningKeyAsync()
            .GetAwaiter().GetResult();  // Cache hit = no I/O
        
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = _cryptoProviderFactory
        };
        
        // ... rest of JWT creation
    }
}
```

**Note**: The `.GetAwaiter().GetResult()` is now acceptable because:
1. On first call, `WarmupAsync()` has already loaded the key during startup
2. On subsequent calls, the cache is populated (no I/O)
3. Cache refresh happens during the 5-minute window, initiated by an async caller

---

## Startup Registration

```csharp
// In MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs
services.AddSingleton<ICachedKeyProvider, CachedKeyProvider>();

// In Program.cs or IHostedService
public class KeyCacheWarmupService(ICachedKeyProvider keyProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await keyProvider.WarmupAsync(ct);
    }
    
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

---

## Cache Invalidation Triggers

The cache should be invalidated when:

1. **Key Rotation**: After a new key is activated
2. **Key Revocation**: After a key is revoked
3. **Manual Override**: Admin action to force refresh

```csharp
// Example: In KeyRotationService
public async Task RotateKeyAsync(CancellationToken ct)
{
    // ... create and activate new key ...
    
    // Invalidate cache to pick up new key
    cachedKeyProvider.InvalidateCache();
}
```

---

## Metrics

The cached key provider should emit metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `mrwhooidc.keys.cache_hits` | Counter | Cache hits |
| `mrwhooidc.keys.cache_misses` | Counter | Cache misses |
| `mrwhooidc.keys.refresh_duration` | Histogram | Time to refresh cache |

---

## Thread Safety

The implementation is thread-safe:
- SemaphoreSlim protects cache updates
- Double-check locking pattern prevents redundant refreshes
- Cache reads are lock-free (volatile semantics via reference assignment)

---

## Dependencies

- `IKeyStore` - underlying key persistence
- `ILogger<T>` - logging
- `TimeProvider` - clock abstraction for testability
