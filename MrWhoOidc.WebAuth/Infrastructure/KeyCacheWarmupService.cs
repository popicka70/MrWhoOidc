using MrWhoOidc.Auth.Services.KeyManagement;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// Warms up the signing key cache on startup to ensure the first request doesn't hit the DB for keys.
/// </summary>
public sealed class KeyCacheWarmupService(
    ICachedKeyProvider keyProvider,
    ILogger<KeyCacheWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Warming up signing key cache...");
        try
        {
            // This will trigger the initial load and caching of the active signing key
            await keyProvider.GetActiveSigningKeyAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Signing key cache warmed up successfully.");
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "Tenant context required", StringComparison.Ordinal))
        {
            logger.LogInformation("Signing key cache warmup deferred until the default tenant is initialized.");
        }
        catch (Exception ex)
        {
            // We don't want to block startup if key loading fails (e.g. DB not ready yet),
            // but we should log it. The first request will try again.
            logger.LogWarning(ex, "Failed to warm up signing key cache. It will be loaded on first request.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
