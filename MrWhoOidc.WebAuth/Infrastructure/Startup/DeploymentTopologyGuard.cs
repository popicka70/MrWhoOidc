using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Infrastructure.Startup;

/// <summary>
/// Guards against running several replicas while security-relevant state lives in process memory.
/// Without Redis the DPoP/JAR replay caches, DPoP nonces, token-exchange / distributed rate limits,
/// the login continuation store, sessions and the HybridCache L2 all fall back to per-process memory.
/// With more than one replica that silently weakens replay protection and rate limiting
/// (each replica only sees its own traffic) and breaks login continuations that land on another pod.
/// </summary>
public static class DeploymentTopologyGuard
{
    public const string MultiInstanceKey = "Deployment:MultiInstance";

    /// <summary>State that falls back to in-memory storage when Redis is not configured.</summary>
    public static readonly IReadOnlyList<string> InMemoryFallbacks =
    [
        "DPoP replay cache",
        "DPoP nonce store",
        "JAR (request object) replay cache",
        "token exchange rate limiter",
        "distributed rate limiter",
        "distributed cache (sessions, login continuations)",
        "HybridCache L2",
        "correlation state cache"
    ];

    /// <summary>
    /// Validates the deployment topology. Throws when <c>Deployment:MultiInstance=true</c> and Redis is not
    /// configured; logs a warning whenever the in-memory fallbacks are active.
    /// </summary>
    public static void Validate(IConfiguration configuration, bool redisConfigured, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (redisConfigured)
        {
            return;
        }

        if (configuration.GetValue<bool>(MultiInstanceKey))
        {
            throw new InvalidOperationException(
                "Deployment:MultiInstance=true but no Redis connection string (ConnectionStrings:redis) is configured. "
                + "Running more than one replica without Redis leaves replay caches, rate limits and login state "
                + "per-process, which weakens DPoP/JAR replay protection and rate limiting. "
                + "Configure ConnectionStrings:redis, or set Deployment:MultiInstance=false and run a single replica.");
        }

        logger.LogWarning(
            "Redis is not configured: in-memory fallbacks are active for {Fallbacks}. "
            + "This is only safe with a single replica; configure ConnectionStrings:redis before scaling out "
            + "(set Deployment:MultiInstance=true to enforce this at startup).",
            string.Join(", ", InMemoryFallbacks));
    }
}
