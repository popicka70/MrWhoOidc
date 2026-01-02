using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;

namespace MrWhoOidc.Auth.MultiTenancy;

public class MultiTenancyStateInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMultiTenancyStateProvider _stateProvider;
    private readonly ILogger<MultiTenancyStateInitializer> _logger;

    public MultiTenancyStateInitializer(
        IServiceProvider serviceProvider, 
        IMultiTenancyStateProvider stateProvider,
        ILogger<MultiTenancyStateInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _stateProvider = stateProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try 
        {
            using var scope = _serviceProvider.CreateScope();
            var licenseService = scope.ServiceProvider.GetRequiredService<ILicenseService>();
            
            var license = await licenseService.GetCurrentLicenseAsync(null, cancellationToken);
            if (license != null)
            {
                var enabled = license.DeploymentMode == DeploymentMode.MultiTenant;
                _stateProvider.UpdateState(enabled);
                _logger.LogInformation(
                    "Multi-tenancy state initialized from license deployment mode {DeploymentMode}: {Enabled}",
                    license.DeploymentMode,
                    enabled);
            }
            else
            {
                _stateProvider.UpdateState(false);
                _logger.LogInformation("No license found. Multi-tenancy disabled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize multi-tenancy state from license.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
