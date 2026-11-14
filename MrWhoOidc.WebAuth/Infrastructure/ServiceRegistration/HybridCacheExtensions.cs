using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers HybridCache with L1 (in-memory) and the configured L2 distributed cache.
/// The L2 is whatever <c>IDistributedCache</c> registration <c>AddMrWhoOidcDistributedState</c>
/// installed (database-backed by default, Redis when selected).
/// </summary>
public static class HybridCacheExtensions
{
    public static IServiceCollection AddMrWhoOidcHybridCache(
        this IServiceCollection services,
        IConfiguration configuration)
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

        // The L2 distributed cache is the IDistributedCache registered by the distributed state
        // extension. HybridCache consumes it automatically; when the Memory provider is selected
        // the L2 and L1 are both in-process.
        return services;
    }
}
