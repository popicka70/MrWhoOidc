using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// Periodically removes expired rows from the database-backed store. Redis and memory stores
/// expire entries themselves and return 0, so this worker is only registered for the database
/// provider.
/// </summary>
internal sealed class DistributedStateCleanupService(
    IDistributedStateStore store,
    IOptions<DistributedStateOptions> options,
    ILogger<DistributedStateCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 2_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.CleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var removed = await store.DeleteExpiredAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} expired distributed state entries.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Distributed state cleanup failed; the next run will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
