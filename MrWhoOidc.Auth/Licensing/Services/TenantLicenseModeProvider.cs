using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Services;

/// <summary>
/// Provider for tenant license mode that directly accesses the database
/// to avoid circular dependencies with TenantService.
/// </summary>
internal sealed class TenantLicenseModeProvider : ITenantLicenseModeProvider
{
    private readonly AuthDbContext _dbContext;
    private readonly ILogger<TenantLicenseModeProvider> _logger;

    public TenantLicenseModeProvider(
        AuthDbContext dbContext,
        ILogger<TenantLicenseModeProvider> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TenantLicenseMode> GetLicenseModeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            var tenant = await _dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.LicenseMode })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (tenant is null)
            {
                _logger.LogDebug("Tenant {TenantId} not found, defaulting to InheritPlatform mode.", tenantId);
                return TenantLicenseMode.InheritPlatform;
            }

            return tenant.LicenseMode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving license mode for tenant {TenantId}, defaulting to InheritPlatform.", tenantId);
            return TenantLicenseMode.InheritPlatform;
        }
    }
}
