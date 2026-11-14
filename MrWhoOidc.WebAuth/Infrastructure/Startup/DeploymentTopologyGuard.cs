using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.WebAuth.Infrastructure.Startup;

/// <summary>
/// Guards against running several replicas while security-relevant state lives in process memory.
/// The database and Redis providers share state between replicas; the memory provider does not.
/// With the memory provider the DPoP/JAR replay caches, DPoP nonces, token-exchange / distributed
/// rate limits, login continuations, sessions and the HybridCache L2 are all per-process, which
/// silently weakens replay protection and rate limiting and breaks state that lands on another pod.
/// </summary>
public static class DeploymentTopologyGuard
{
    public const string MultiInstanceKey = "Deployment:MultiInstance";

    /// <summary>State that is per-process when <see cref="DistributedStateProvider.Memory"/> is selected.</summary>
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
    /// Validates the deployment topology. Throws when <c>Deployment:MultiInstance=true</c> and the
    /// memory provider is selected; logs a warning whenever the in-memory fallbacks are active.
    /// </summary>
    public static void Validate(
        IConfiguration configuration,
        DistributedStateProvider provider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (provider != DistributedStateProvider.Memory)
        {
            return;
        }

        if (configuration.GetValue<bool>(MultiInstanceKey))
        {
            throw new InvalidOperationException(
                "Deployment:MultiInstance=true but DistributedState:Provider=Memory. "
                + "Running more than one replica without shared state leaves replay caches, rate limits and "
                + "login state per-process, which weakens DPoP/JAR replay protection and rate limiting. "
                + "Use DistributedState:Provider=Database (default) or Redis, or set Deployment:MultiInstance=false "
                + "and run a single replica.");
        }

        logger.LogWarning(
            "DistributedState:Provider=Memory: in-memory fallbacks are active for {Fallbacks}. "
            + "This is only safe with a single replica; use the database (default) or Redis provider before "
            + "scaling out (set Deployment:MultiInstance=true to enforce this at startup).",
            string.Join(", ", InMemoryFallbacks));
    }
}
