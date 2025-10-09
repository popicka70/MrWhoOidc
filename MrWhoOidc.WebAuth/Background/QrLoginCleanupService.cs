using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Background;

public sealed class QrLoginCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<QrLoginCleanupService> _logger;
    private readonly QrLoginOptions _options;

    public QrLoginCleanupService(
        IServiceProvider serviceProvider,
        ILogger<QrLoginCleanupService> logger,
        IOptions<QrLoginOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("QR login cleanup service is disabled");
            return;
        }

        _logger.LogInformation("QR login cleanup service starting");

        // Startup delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.CleanupIntervalSeconds), stoppingToken);

                using var scope = _serviceProvider.CreateScope();

                // Set tenant context for background operation
                if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, stoppingToken))
                {
                    _logger.LogWarning("QR login cleanup skipped: default tenant not found");
                    continue;
                }

                var qrService = scope.ServiceProvider.GetRequiredService<IQrLoginService>();

                var gracePeriod = TimeSpan.FromSeconds(_options.CleanupGracePeriodSeconds);
                var olderThan = DateTimeOffset.UtcNow.Subtract(gracePeriod);

                var count = await qrService.CleanupExpiredSessionsAsync(olderThan);

                if (count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired QR login sessions", count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during QR login session cleanup");
            }
        }

        _logger.LogInformation("QR login cleanup service stopped");
    }
}
