using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction: metrics + token metrics recorder safety registration.
/// Mirrors original Program.cs logic exactly (adds default recorder and safety duplication check).
/// </summary>
public static class MetricsExtensions
{
    public static IServiceCollection AddMrWhoOidcMetrics(this IServiceCollection services)
    {
        services.AddSingleton<OidcEndpointMetrics>();
        services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcEndpointMetrics>());

        if (!services.Any(d => d.ServiceType == typeof(ITokenMetricsRecorder)))
        {
            services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
        }
        return services;
    }
}
