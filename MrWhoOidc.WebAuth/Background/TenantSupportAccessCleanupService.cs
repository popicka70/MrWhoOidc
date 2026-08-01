using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Background;

namespace MrWhoOidc.WebAuth.Background;

/// <summary>
/// Background cleanup service for Tenant Support Access sessions.
/// Periodically queries the database for active sessions that have expired
/// and transitions their status to Expired.
/// </summary>
public sealed class TenantSupportAccessCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantSupportAccessCleanupService> _logger;

    public TenantSupportAccessCleanupService(
        IServiceProvider serviceProvider,
        ILogger<TenantSupportAccessCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Tenant Support Access cleanup service starting");

        // Initial delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var interval = TimeSpan.FromHours(1);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await Task.Delay(interval, stoppingToken);

                using var scope = _serviceProvider.CreateScope();

                // Set tenant context for background operation
                if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, stoppingToken))
                {
                    _logger.LogWarning("Tenant Support Access cleanup skipped: default tenant not found");
                    continue;
                }

                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

                var now = DateTimeOffset.UtcNow;

                // Find active sessions where ExpiresAt has passed
                var expiredSessions = await db.TenantSupportAccessSessions
                    .Where(s => s.Status == SupportAccessStatus.Active && s.ExpiresAt < now)
                    .ToListAsync(stoppingToken);

                if (expiredSessions.Count > 0)
                {
                    _logger.LogInformation("Found {Count} expired support access sessions to clean up", expiredSessions.Count);

                    foreach (var session in expiredSessions)
                    {
                        // Load current state from DB for concurrency-safe update
                        var current = await db.TenantSupportAccessSessions
                            .FirstOrDefaultAsync(s => s.Id == session.Id, stoppingToken);

                        if (current != null && current.Status == SupportAccessStatus.Active)
                        {
                            current.Status = SupportAccessStatus.Expired;
                            current.ConcurrencyToken = GuidHelper.NewId();

                            await db.SaveChangesAsync(stoppingToken);
                        }
                    }

                    _logger.LogInformation("Cleaned up {Count} expired support access sessions", expiredSessions.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Tenant Support Access cleanup");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await SafeWaitAsync(timer, stoppingToken));

        _logger.LogInformation("Tenant Support Access cleanup service stopped");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
