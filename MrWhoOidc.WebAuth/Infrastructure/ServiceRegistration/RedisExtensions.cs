using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers a shared Redis <see cref="IConnectionMultiplexer"/> if a redis connection string is configured.
/// Returns the created multiplexer (or null) so calling code can branch on availability.
/// Synchronous connect is used intentionally during startup to fail fast if misconfigured.
/// </summary>
public static class RedisExtensions
{
    public static IConnectionMultiplexer? AddMrWhoOidcRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConnection = configuration.GetConnectionString("redis") ?? configuration["ConnectionStrings:redis"];   
        if (string.IsNullOrWhiteSpace(redisConnection)) return null;
        var mux = ConnectionMultiplexer.Connect(redisConnection); // fail fast if invalid
        services.AddSingleton<IConnectionMultiplexer>(mux);
        return mux;
    }
}
