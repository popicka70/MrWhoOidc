using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

public static class CorrelationExtensions
{
    public static IServiceCollection AddMrWhoOidcCorrelation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ICorrelationIdGenerator, CorrelationIdGenerator>();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<ICorrelationStateCache>(sp => new CorrelationStateCache(
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IDistributedStateStore>(),
            sp.GetRequiredService<ILogger<CorrelationStateCache>>(),
            sp.GetRequiredService<IOidcMetrics>(),
            sp.GetRequiredService<ICorrelationIdGenerator>()));
        return services;
    }
}
