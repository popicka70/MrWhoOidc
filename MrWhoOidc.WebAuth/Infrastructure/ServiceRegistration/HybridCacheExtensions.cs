using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers HybridCache with L1 (in-memory) and optional L2 (Redis) distributed caching.
/// HybridCache is a .NET 9 feature that combines memory and distributed caching with stampede protection,
/// better performance, and a simpler API compared to IMemoryCache + IDistributedCache.
/// </summary>
public static class HybridCacheExtensions
{
    /// <summary>
    /// Adds HybridCache with optional Redis L2 backend.
    /// If Redis is available, it will be used as the L2 distributed cache.
    /// If not, HybridCache will operate in L1-only mode (memory-only).
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">App configuration</param>
    /// <param name="redisMux">Optional Redis connection multiplexer for L2 caching</param>
    /// <returns>IServiceCollection for chaining</returns>
    public static IServiceCollection AddMrWhoOidcHybridCache(
        this IServiceCollection services,
        IConfiguration configuration,
        IConnectionMultiplexer? redisMux)
    {
        // Register HybridCache with configuration
        services.AddHybridCache(options =>
        {
            // L1 (in-memory) cache settings
            options.MaximumPayloadBytes = 1024 * 1024; // 1 MB max per entry
            options.MaximumKeyLength = 512; // Max key length

            // Read custom settings from configuration if available
            var cacheSection = configuration.GetSection("HybridCache");

            TimeSpan expiration = TimeSpan.FromMinutes(5); // Default
            TimeSpan localExpiration = TimeSpan.FromMinutes(5); // Default

            if (cacheSection.Exists())
            {
                var maxPayloadMb = cacheSection.GetValue<int?>("MaximumPayloadMB");
                if (maxPayloadMb.HasValue)
                {
                    options.MaximumPayloadBytes = maxPayloadMb.Value * 1024 * 1024;
                }

                var defaultExpirationMinutes = cacheSection.GetValue<int?>("DefaultExpirationMinutes");
                if (defaultExpirationMinutes.HasValue)
                {
                    expiration = TimeSpan.FromMinutes(defaultExpirationMinutes.Value);
                    localExpiration = TimeSpan.FromMinutes(defaultExpirationMinutes.Value);
                }
            }

            // Set default expiration with init-only properties
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = expiration,
                LocalCacheExpiration = localExpiration
            };
        });

        // If Redis is available, configure it as the L2 distributed cache for HybridCache
        if (redisMux != null)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.ConnectionMultiplexerFactory = () => Task.FromResult(redisMux)!;
                options.InstanceName = "mrwhooidc:"; // Prefix for all Redis keys
            });
        }

        return services;
    }
}
