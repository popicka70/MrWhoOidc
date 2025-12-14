using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestSupport;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Helpers;

public static class ExternalOidcTestCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the common infrastructure required by the external OIDC handler in unit tests.
    /// This intentionally does not register auth-core services (ClientStore/UserAccountService/etc.).
    /// Pair with <see cref="ExternalOidcTestServiceCollectionExtensions.AddExternalOidcTestDefaults"/>.
    /// </summary>
    public static IServiceCollection AddExternalOidcTestCore(
        this IServiceCollection services,
        string inMemoryDbName,
        bool useEphemeralDataProtectionProvider = true,
        bool useRecordingMetrics = false)
    {
        services.AddLogging();
        services.AddMemoryCache();

        if (useEphemeralDataProtectionProvider)
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }
        else
        {
            services.AddDataProtection();
        }

        services.AddHttpClient();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(inMemoryDbName));

        // Most tests pass Configuration directly to AddMrWhoOidcCorrelation; this is here for any services that may resolve IConfiguration.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddScoped<IClaimMappingService, ClaimMappingService>();
        services.AddSingleton<IJwksCache, JwksCache>();

        if (useRecordingMetrics)
        {
            services.AddSingleton<RecordingOidcMetrics>();
            services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<RecordingOidcMetrics>());
        }
        else
        {
            services.AddSingleton<OidcMetrics>();
            services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcMetrics>());
        }

        services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
        services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();

        services.AddMrWhoOidcCorrelation(new ConfigurationBuilder().Build(), redisMux: null);

        // Register ITenantAccessor for multi-tenant support
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithDefaultTenant());

        return services;
    }
}
