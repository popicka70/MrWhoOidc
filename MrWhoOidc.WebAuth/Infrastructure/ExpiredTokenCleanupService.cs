using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Background;

namespace MrWhoOidc.WebAuth.Infrastructure;

// Periodically deletes expired tokens from persistence to keep the table small.
// Cleans up:
// - access (opaque) tokens with ExpiresAt < now
// - refresh tokens with ExpiresAt < now
internal sealed class ExpiredTokenCleanupService(IServiceProvider services, ILogger<ExpiredTokenCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run shortly after startup, then periodically
        var initialDelay = TimeSpan.FromMinutes(1);
        var interval = TimeSpan.FromHours(1);

        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = services.CreateScope();

                // Set tenant context for background operation
                if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, stoppingToken))
                {
                    logger.LogWarning("Expired token cleanup skipped: default tenant not found");
                    continue;
                }

                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

                var now = DateTimeOffset.UtcNow;

                // ⚡ Bolt Optimization: Use ExecuteDeleteAsync() for bulk deletion
                // This eliminates fetching tokens into memory and sending multi-round-trip DELETE commands.
                // EF Core translates this directly into a single efficient DELETE SQL statement.

                // Delete expired access tokens
                var expiredAccessCount = await db.Tokens
                    .Where(t => t.Type == "access" && t.ExpiresAt < now)
                    .ExecuteDeleteAsync(stoppingToken);

                // Delete expired refresh tokens
                var expiredRefreshCount = await db.Tokens
                    .Where(t => t.Type == "refresh" && t.ExpiresAt < now)
                    .ExecuteDeleteAsync(stoppingToken);

                var total = expiredAccessCount + expiredRefreshCount;
                if (total > 0)
                {
                    logger.LogInformation("Expired token cleanup removed {Total} tokens (access={Access}, refresh={Refresh})", total, expiredAccessCount, expiredRefreshCount);
                }
            }
            catch (OperationCanceledException)
            {
                // graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expired token cleanup failed");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
