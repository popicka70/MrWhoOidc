using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

/// <summary>
/// Simple interface for retrieving tenant license configuration.
/// Used to avoid circular dependencies between LicenseService and TenantService.
/// </summary>
public interface ITenantLicenseModeProvider
{
    /// <summary>
    /// Gets the license mode for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant's license mode, or InheritPlatform if tenant not found.</returns>
    Task<TenantLicenseMode> GetLicenseModeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
