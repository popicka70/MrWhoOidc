using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MrWhoOidc.WebAuth.Observability;

public static class ServiceCollectionMetricsExtensions
{
    public static IServiceCollection AddOidcMetricsIfMissing(this IServiceCollection services)
    {
        services.TryAddSingleton<IOidcMetrics, NoOpOidcMetrics>();
        return services;
    }
}
