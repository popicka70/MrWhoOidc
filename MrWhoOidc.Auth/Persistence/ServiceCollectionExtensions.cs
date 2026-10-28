using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.Auth.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddAuthPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.AddScoped<ISectorIdentifierResolver, SectorIdentifierResolver>();
        services.AddScoped<IPairwiseSubjectService, PairwiseSubjectService>();

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
            // Use a unique database name per test instance to avoid conflicts when tests run in parallel
            var dbName = configuration["Testing:InMemoryDbName"] ?? $"authdb-test-{Guid.NewGuid():N}";
            services.AddDbContext<AuthDbContext>((sp, opts) => opts
                    .UseInMemoryDatabase(dbName)
                    .AddInterceptors(sp.GetServices<ISaveChangesInterceptor>())
                    // The in-memory store has no transactions; the grant/import paths open one anyway, so the
                    // "transactions are not supported" warning is suppressed instead of failing the request.
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
                contextLifetime: ServiceLifetime.Scoped,
                optionsLifetime: ServiceLifetime.Singleton);
            RegisterDbContextFactory(services);
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
                var dbName = configuration["Testing:InMemoryDbName"] ?? $"authdb-test-missing-{Guid.NewGuid():N}";
                services.AddDbContext<AuthDbContext>((sp, opts) => opts
                        .UseInMemoryDatabase(dbName)
                        .AddInterceptors(sp.GetServices<ISaveChangesInterceptor>())
                        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
                    contextLifetime: ServiceLifetime.Scoped,
                    optionsLifetime: ServiceLifetime.Singleton);
                RegisterDbContextFactory(services);
                return services;
            }
            throw new InvalidOperationException("PostgreSQL connection string for 'authdb' was not found in configuration.");
        }

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        // Configure DbContext options once and register them as Singleton so the factory (singleton) can consume them
        services.AddDbContext<AuthDbContext>((sp, options) =>
        {
            // Hosts add cross-cutting write guards (e.g. WebAuth's platform-realm guard) as interceptors.
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.UseNpgsql(cs, npgsql =>
            {
                var x = cs;
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
            });

            // Suppress pending model changes warning - model is in sync but EF Core 9 is overly strict
            options.ConfigureWarnings(warnings =>
                warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }, contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

        RegisterDbContextFactory(services);

        return services;
    }

    /// <summary>
    /// H5: register the context factory with an explicitly owned, tenantless accessor.
    /// <para>
    /// <c>AddDbContextFactory&lt;AuthDbContext&gt;()</c> activates the context against the root service provider.
    /// The scoped <c>ITenantAccessor</c> would then be resolved from the root: with scope validation on that
    /// throws, and with scope validation off every factory-created context shares one mutable accessor. The
    /// replacement factory owns its accessor per context and fails closed until a caller explicitly opens a
    /// cross-tenant scope.
    /// </para>
    /// </summary>
    private static void RegisterDbContextFactory(IServiceCollection services)
    {
        services.RemoveAll<IDbContextFactory<AuthDbContext>>();
        services.AddSingleton<IDbContextFactory<AuthDbContext>>(sp => new TenantlessAuthDbContextFactory(
            sp.GetRequiredService<DbContextOptions<AuthDbContext>>(),
            sp.GetService<ISecretProtector>(),
            sp.GetService<TenantFilterOptions>()));
    }
}
