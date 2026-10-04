using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers a shared Redis <see cref="IConnectionMultiplexer"/> if a redis connection string is configured.
/// Returns the created multiplexer (or null) so calling code can branch on availability.
/// The connection string is parsed eagerly (a malformed string still fails startup), but
/// <c>AbortOnConnectFail</c> is forced off so a temporarily unreachable Redis does not crash the host;
/// the multiplexer keeps reconnecting in the background and Redis-backed stores fail closed meanwhile.
/// </summary>
public static class RedisExtensions
{
    public static IConnectionMultiplexer? AddMrWhoOidcRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConnection = configuration.GetConnectionString("redis") ?? configuration["ConnectionStrings:redis"];
        if (string.IsNullOrWhiteSpace(redisConnection)) return null;
        var options = ConfigurationOptions.Parse(redisConnection); // throws if the connection string is malformed
        options.AbortOnConnectFail = false;
        var mux = ConnectionMultiplexer.Connect(options);
        services.AddSingleton<IConnectionMultiplexer>(mux);
        return mux;
    }
}
