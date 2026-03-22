using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Background service that monitors client secrets for upcoming expiration and emits warnings.
/// Helps prevent service disruption by alerting operators before secrets expire.
/// </summary>
internal sealed class ClientSecretExpiryMonitor(
    IServiceProvider services,
    IOptions<ClientSecretExpiryMonitorOptions> options,
    IClientSecretMetrics? metrics,
    ILogger<ClientSecretExpiryMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Client secret expiry monitoring is disabled.");
            return;
        }

        // Startup delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);

        // Initial run on startup
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        // Periodic checks (default: daily)
        var period = opts.CheckPeriod <= TimeSpan.Zero ? TimeSpan.FromHours(24) : opts.CheckPeriod;
        using var timer = new PeriodicTimer(period);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            var now = DateTime.UtcNow;
            var warningThreshold = now.AddDays(7); // Warn for secrets expiring within 7 days

            // Query secrets expiring soon
            var expiringSecrets = await db.ClientSecrets
                .Include(s => s.Client)
                .Where(s => s.ActivatedAtUtc != null
                         && s.RevokedAtUtc == null
                         && s.ExpiresAtUtc != null
                         && s.ExpiresAtUtc > now
                         && s.ExpiresAtUtc <= warningThreshold)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (expiringSecrets.Count > 0)
            {
                logger.LogWarning(
                    "Found {Count} client secrets expiring within 7 days. Review and rotate these secrets.",
                    expiringSecrets.Count);

                var minDays = double.MaxValue;
                foreach (var secret in expiringSecrets)
                {
                    var daysUntilExpiry = (secret.ExpiresAtUtc!.Value - now).TotalDays;
                    if (daysUntilExpiry < minDays)
                        minDays = daysUntilExpiry;

                    logger.LogWarning(
                        "Client secret expiring soon: ClientId={ClientId}, SecretId={SecretId}, Description={Description}, ExpiresAt={ExpiresAt}, DaysRemaining={DaysRemaining:F1}",
                        secret.Client.ClientId,
                        secret.Id,
                        secret.Description ?? "(no description)",
                        secret.ExpiresAtUtc,
                        daysUntilExpiry);
                }

                // Update metrics with minimum days until expiry
                metrics?.SetDaysUntilExpiry(minDays);
            }

            // Count total active secrets across all clients
            var totalActiveSecrets = await db.ClientSecrets
                .AsNoTracking()
                .Where(s => s.ActivatedAtUtc != null
                         && s.RevokedAtUtc == null
                         && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now))
                .CountAsync(ct)
                .ConfigureAwait(false);

            metrics?.SetActiveSecretsCount(totalActiveSecrets);

            // Check for fully expired clients (all secrets expired or revoked)
            var clientsWithSecrets = await db.Clients
                .Include(c => c.ClientSecrets)
                .Where(c => c.ClientSecrets.Any())
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var client in clientsWithSecrets)
            {
                var hasAnyActiveSecret = client.ClientSecrets.Any(s =>
                    s.ActivatedAtUtc != null
                    && s.RevokedAtUtc == null
                    && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now));

                if (!hasAnyActiveSecret)
                {
                    logger.LogError(
                        "CRITICAL: Client has NO active secrets - authentication will fail: ClientId={ClientId}, TenantId={TenantId}",
                        client.ClientId,
                        client.TenantId);
                }
            }

            logger.LogDebug("Client secret expiry check completed successfully.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during client secret expiry monitoring");
        }
    }
}

/// <summary>
/// Configuration options for client secret expiry monitoring.
/// </summary>
public sealed class ClientSecretExpiryMonitorOptions
{
    /// <summary>
    /// Whether the expiry monitor is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to check for expiring secrets. Default is 24 hours.
    /// </summary>
    public TimeSpan CheckPeriod { get; set; } = TimeSpan.FromHours(24);
}
