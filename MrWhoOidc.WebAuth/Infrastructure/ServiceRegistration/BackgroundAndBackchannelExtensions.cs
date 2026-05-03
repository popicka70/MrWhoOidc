using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Background;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction: background cleanup workers (expired tokens, PAR) and backchannel logout dispatcher + related runtime state.
/// Mechanical move from Program.cs with no behavioral changes.
/// </summary>
public static class BackgroundAndBackchannelExtensions
{
    public static IServiceCollection AddMrWhoOidcBackgroundAndBackchannel(this IServiceCollection services, IConfiguration configuration)
    {
        // Skip background services in test mode for faster test execution
        if (configuration.GetValue<bool>("Testing:DisableBackgroundServices"))
        {
            // Still register the singletons needed by other services
            services.AddSingleton(new BackchannelDispatchOptions());
            services.Configure<BackchannelFeatureOptions>(configuration.GetSection("Backchannel"));
            services.AddSingleton<BackchannelRuntimeState>();
            return services;
        }

        // Background cleanup for expired tokens (opaque + refresh)
        services.AddHostedService<ExpiredTokenCleanupService>();
        // PAR cleanup
        services.AddHostedService<ParCleanupHostedService>();

        // Backchannel Logout dispatcher + options/state
        services.AddSingleton(new BackchannelDispatchOptions());
        services.Configure<BackchannelFeatureOptions>(configuration.GetSection("Backchannel"));
        services.AddSingleton<BackchannelRuntimeState>();
        services.AddHostedService<BackchannelLogoutDispatcher>();

        // QR login cleanup service
        services.AddHostedService<QrLoginCleanupService>();

        // Key cache warmup
        services.AddHostedService<MrWhoOidc.WebAuth.Infrastructure.KeyCacheWarmupService>();

        return services;
    }
}
