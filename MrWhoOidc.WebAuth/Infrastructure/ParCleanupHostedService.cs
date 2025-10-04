using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.WebAuth.Background;

namespace MrWhoOidc.WebAuth.Infrastructure;

public sealed class ParCleanupHostedService(IServiceProvider services, ILogger<ParCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        
        // Run every 5 minutes
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                
                // Set tenant context for background operation
                if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, stoppingToken))
                {
                    logger.LogWarning("PAR cleanup skipped: default tenant not found");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }
                
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                var now = DateTimeOffset.UtcNow;
                var expired = await db.PushedAuthorizationRequests.Where(p => p.ExpiresAt < now || p.Consumed).ToListAsync(stoppingToken);
                if (expired.Count > 0)
                {
                    db.PushedAuthorizationRequests.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogInformation("PAR cleanup removed {Count} entries", expired.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PAR cleanup failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (TaskCanceledException) { }
        }
    }
}
