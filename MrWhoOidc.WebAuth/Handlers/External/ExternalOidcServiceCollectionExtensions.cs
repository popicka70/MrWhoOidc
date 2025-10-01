using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Extension methods for registering external OIDC handler services.
/// </summary>
public static class ExternalOidcServiceCollectionExtensions
{
    /// <summary>
    /// Registers all external OIDC handler services with the DI container.
    /// </summary>
    public static IServiceCollection AddExternalOidcHandler(this IServiceCollection services)
    {
        // Core handler
        services.AddScoped<IExternalOidcHandler, ExternalOidcHandler>();

        // Specialized services
        services.AddScoped<IExternalOidcStateManager, ExternalOidcStateManager>();
        services.AddScoped<IExternalOidcCorrelationManager, ExternalOidcCorrelationManager>();
        services.AddScoped<IExternalOidcDiscoveryService, ExternalOidcDiscoveryService>();
        services.AddScoped<IExternalOidcRequestBuilder, ExternalOidcRequestBuilder>();
        services.AddScoped<IExternalOidcTokenExchangeService, ExternalOidcTokenExchangeService>();
        services.AddScoped<IExternalOidcTokenValidator, ExternalOidcTokenValidator>();
        services.AddScoped<IExternalOidcUserProvisioner, ExternalOidcUserProvisioner>();
        services.AddScoped<IExternalOidcSessionManager, ExternalOidcSessionManager>();
        services.AddScoped<IExternalOidcErrorHandler, ExternalOidcErrorHandler>();
        services.AddScoped<IExternalOidcMetricsRecorder, ExternalOidcMetricsRecorder>();

        return services;
    }
}
