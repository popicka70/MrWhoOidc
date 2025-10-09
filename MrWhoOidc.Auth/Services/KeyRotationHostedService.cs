using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Auth.Services;

internal sealed class KeyRotationHostedService(
    IServiceProvider services,
    IOptions<KeyRotationOptions> options,
    ILogger<KeyRotationHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Key rotation is disabled.");
            return;
        }

        // Startup delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        // Initial run on startup
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        // Periodic checks
        var period = opts.CheckPeriod <= TimeSpan.Zero ? TimeSpan.FromHours(1) : opts.CheckPeriod;
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

            // Set tenant context for background operation
            if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, ct).ConfigureAwait(false))
            {
                logger.LogWarning("Key rotation skipped: default tenant not found");
                return;
            }

            var rotation = scope.ServiceProvider.GetRequiredService<IKeyRotationService>();
            await rotation.EnsureInitializedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Key rotation check failed.");
        }
    }
}
