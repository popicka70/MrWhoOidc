using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.WebAuth.Infrastructure;

public sealed class ParCleanupHostedService(IServiceProvider services, ILogger<ParCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run every 5 minutes
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
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
