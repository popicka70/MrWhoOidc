using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Services;

namespace MrWhoOidc.WebAuth.Background;

public sealed class LicenseValidationWorker : BackgroundService
{
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LicenseValidationWorker> _logger;

    public LicenseValidationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<LicenseValidationWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("License validation worker started with interval {Interval}", ValidationInterval);

        await RunValidationAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ValidationInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunValidationAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunValidationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, cancellationToken).ConfigureAwait(false);

            var licenseService = scope.ServiceProvider.GetRequiredService<ILicenseService>();
            var license = await licenseService.GetCurrentLicenseAsync(null, cancellationToken).ConfigureAwait(false);

            if (license is null)
            {
                _logger.LogWarning("License validation worker did not find an active license.");
            }
        }
        catch (OperationCanceledException)
        {
            // Allow graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "License validation worker failed during validation cycle.");
        }
    }
}
