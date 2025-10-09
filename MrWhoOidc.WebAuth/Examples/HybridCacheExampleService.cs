using Microsoft.Extensions.Caching.Hybrid;

namespace MrWhoOidc.WebAuth.Examples;

/// <summary>
/// Example service demonstrating HybridCache usage patterns.
/// This is a reference implementation showing best practices for caching in MrWhoOidc.
/// </summary>
public class HybridCacheExampleService
{
    private readonly HybridCache _cache;
    private readonly ILogger<HybridCacheExampleService> _logger;

    public HybridCacheExampleService(
        HybridCache cache,
        ILogger<HybridCacheExampleService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Example 1: Basic cache usage with default expiration
    /// </summary>
    public async Task<string> GetBasicDataAsync(string key, CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync(
            $"example:basic:{key}",
            async cancel =>
            {
                _logger.LogInformation("Cache miss for key {Key}, fetching from source", key);
                await Task.Delay(100, cancel); // Simulate expensive operation
                return $"Data for {key}";
            },
            cancellationToken: ct
        );
    }

    /// <summary>
    /// Example 2: Cache with custom expiration
    /// Short L1 (local memory) expiration, longer L2 (Redis) expiration
    /// </summary>
    public async Task<UserProfile> GetUserProfileAsync(string userId, CancellationToken ct = default)
    {
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10),          // L2 (Redis) TTL
            LocalCacheExpiration = TimeSpan.FromMinutes(2)  // L1 (memory) TTL
        };

        return await _cache.GetOrCreateAsync(
            $"user:profile:{userId}",
            async cancel =>
            {
                _logger.LogInformation("Fetching user profile for {UserId}", userId);
                // Simulate DB query
                await Task.Delay(50, cancel);
                return new UserProfile { Id = userId, Name = $"User {userId}" };
            },
            options,
            cancellationToken: ct
        );
    }

    /// <summary>
    /// Example 3: Cache with tags for bulk invalidation
    /// Tags allow invalidating multiple related cache entries at once
    /// </summary>
    public async Task<List<ClientInfo>> GetClientsByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(15),
            LocalCacheExpiration = TimeSpan.FromMinutes(5)
        };

        // Tags are passed as a separate parameter
        var tags = new List<string> { $"tenant:{tenantId}", "clients" };

        return await _cache.GetOrCreateAsync(
            $"clients:tenant:{tenantId}",
            async cancel =>
            {
                _logger.LogInformation("Fetching clients for tenant {TenantId}", tenantId);
                await Task.Delay(100, cancel);
                return new List<ClientInfo>
                {
                    new() { Id = "client1", TenantId = tenantId },
                    new() { Id = "client2", TenantId = tenantId }
                };
            },
            options,
            tags,
            cancellationToken: ct
        );
    }

    /// <summary>
    /// Example 4: Manual cache invalidation
    /// </summary>
    public async Task InvalidateUserCacheAsync(string userId, CancellationToken ct = default)
    {
        await _cache.RemoveAsync($"user:profile:{userId}", ct);
        _logger.LogInformation("Invalidated cache for user {UserId}", userId);
    }

    /// <summary>
    /// Example 5: Bulk invalidation by tag
    /// Invalidate all cache entries associated with a tenant
    /// </summary>
    public async Task InvalidateTenantCacheAsync(string tenantId, CancellationToken ct = default)
    {
        await _cache.RemoveByTagAsync($"tenant:{tenantId}", ct);
        _logger.LogInformation("Invalidated all cache entries for tenant {TenantId}", tenantId);
    }

    /// <summary>
    /// Example 6: Conditional caching based on result
    /// Only cache successful results
    /// </summary>
    public async Task<ApiResult?> GetApiResultAsync(string endpoint, CancellationToken ct = default)
    {
        var result = await _cache.GetOrCreateAsync(
            $"api:result:{endpoint}",
            async cancel =>
            {
                _logger.LogInformation("Calling API endpoint {Endpoint}", endpoint);
                await Task.Delay(200, cancel);

                // Simulate API call that might fail
                if (endpoint == "fail")
                {
                    return null; // Don't cache failures
                }

                return new ApiResult { Data = $"Result from {endpoint}" };
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
            cancellationToken: ct
        );

        return result;
    }

    /// <summary>
    /// Example 7: Stampede protection demonstration
    /// Multiple concurrent requests will only execute the factory once
    /// </summary>
    public async Task<string> GetExpensiveDataAsync(string key, CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync(
            $"expensive:{key}",
            async cancel =>
            {
                _logger.LogWarning("EXPENSIVE OPERATION for key {Key} - should only see this once per cache miss", key);
                await Task.Delay(5000, cancel); // 5 second delay
                return $"Expensive result for {key}";
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
            cancellationToken: ct
        );
    }
}

// Example DTOs
public record UserProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public record ClientInfo
{
    public string Id { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
}

public record ApiResult
{
    public string Data { get; init; } = string.Empty;
}
