using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MrWhoOidc.Auth.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddAuthPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("authdb")
                 ?? configuration.GetConnectionString("AuthDb")
                 ?? configuration["ConnectionStrings:authdb"]
                 ?? Environment.GetEnvironmentVariable("AUTHDB__CONNECTIONSTRING")
                 ?? Environment.GetEnvironmentVariable("AUTHDB_CONNECTIONSTRING");

        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException("PostgreSQL connection string for 'authdb' was not found in configuration.");
        }

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        // Configure DbContext options once and register them as Singleton so the factory (singleton) can consume them
        services.AddDbContext<AuthDbContext>(options =>
        {
            options.UseNpgsql(cs, npgsql =>
            {
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
            });
        }, contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

        // Register a factory that uses the same options configuration
        services.AddDbContextFactory<AuthDbContext>();

        return services;
    }
}
