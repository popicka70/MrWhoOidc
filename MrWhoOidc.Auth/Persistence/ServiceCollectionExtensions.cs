using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MrWhoOidc.Auth.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddAuthPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Test/diagnostic bypass: when Testing:UseInMemoryAuthDb=true, short-circuit to in-memory provider
        // This enables lightweight host startup for endpoint surface snapshot tests without requiring a PostgreSQL connection string.
        var useInMemory = configuration["Testing:UseInMemoryAuthDb"];
        var envUseInMem = Environment.GetEnvironmentVariable("AUTHDB_INMEMORY");
        if (string.IsNullOrEmpty(useInMemory) && !string.IsNullOrEmpty(envUseInMem))
        {
            useInMemory = envUseInMem;
        }
        if (string.Equals(useInMemory, "true", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase("authdb-test"), contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);
            services.AddDbContextFactory<AuthDbContext>();
            return services;
        }

        var cs = configuration.GetConnectionString("authdb")
                 ?? configuration.GetConnectionString("AuthDb")
                 ?? configuration["ConnectionStrings:authdb"]
                 ?? Environment.GetEnvironmentVariable("AUTHDB__CONNECTIONSTRING")
                 ?? Environment.GetEnvironmentVariable("AUTHDB_CONNECTIONSTRING");

        if (string.IsNullOrWhiteSpace(cs))
        {
            // Fallback: allow explicit test opt-in to proceed with in-memory db when real connection is absent.
            var allowFallback = configuration["Testing:AllowInMemoryFallback"];
            if (string.Equals(useInMemory, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(allowFallback, "true", StringComparison.OrdinalIgnoreCase))
            {
                services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase("authdb-test-missing"), contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);
                services.AddDbContextFactory<AuthDbContext>();
                return services;
            }
            throw new InvalidOperationException("PostgreSQL connection string for 'authdb' was not found in configuration.");
        }

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        // Configure DbContext options once and register them as Singleton so the factory (singleton) can consume them
        services.AddDbContext<AuthDbContext>(options =>
        {
            options.UseNpgsql(cs, npgsql =>
            {
                var x = cs;
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
            });
        }, contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

        // Register a factory that uses the same options configuration
        services.AddDbContextFactory<AuthDbContext>();

        return services;
    }
}
