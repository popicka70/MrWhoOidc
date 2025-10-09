using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

public static class CorrelationExtensions
{
    public static IServiceCollection AddMrWhoOidcCorrelation(this IServiceCollection services, IConfiguration configuration, IConnectionMultiplexer? redisMux)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ICorrelationIdGenerator, CorrelationIdGenerator>();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<ICorrelationStateCache>(sp =>
        {
            var memory = sp.GetRequiredService<IMemoryCache>();
            var logger = sp.GetRequiredService<ILogger<CorrelationStateCache>>();
            var metrics = sp.GetRequiredService<IOidcMetrics>();
            var generator = sp.GetRequiredService<ICorrelationIdGenerator>();
            return new CorrelationStateCache(memory, redisMux, logger, metrics, generator);
        });
        return services;
    }
}
