using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MrWhoOidc.Auth.Persistence;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.Health;

/// <summary>Health check tag selecting the checks evaluated by <c>/health/ready</c>.</summary>
public static class ReadinessTags
{
    public const string Ready = "ready";
}

/// <summary>
/// Readiness: the database is reachable and (for relational providers) has no pending migrations.
/// </summary>
public sealed class DatabaseReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy("Database connection failed.");
        }

        if (db.Database.IsRelational())
        {
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Any())
            {
                return HealthCheckResult.Unhealthy("Database has pending migrations.");
            }
        }

        return HealthCheckResult.Healthy();
    }
}

/// <summary>
/// Readiness: the configured Redis server answers a PING. Registered only when Redis is configured.
/// </summary>
public sealed class RedisReadinessHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!redis.IsConnected)
        {
            return HealthCheckResult.Unhealthy("Redis is not connected.");
        }

        await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

public static class ReadinessHealthCheckExtensions
{
    /// <summary>
    /// Registers the readiness checks used by <c>/health/ready</c>: database connectivity + migrations,
    /// and Redis connectivity when (and only when) Redis is configured.
    /// </summary>
    public static IServiceCollection AddMrWhoOidcReadinessChecks(this IServiceCollection services, IConnectionMultiplexer? redisMux)
    {
        var checks = services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>("database", tags: [ReadinessTags.Ready]);

        if (redisMux is not null)
        {
            checks.AddCheck<RedisReadinessHealthCheck>("redis", tags: [ReadinessTags.Ready]);
        }

        return services;
    }
}
