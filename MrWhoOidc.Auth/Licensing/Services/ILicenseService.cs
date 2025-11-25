using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

public interface ILicenseService
{
    /// <summary>
    /// Gets the currently installed license for the specified tenant.
    /// Does not consider tenant license inheritance mode.
    /// </summary>
    Task<LicenseInfo?> GetCurrentLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the effective license for a tenant, taking into account the tenant's license mode.
    /// - If tenant uses InheritPlatform mode: returns platform license projected to tenant scope
    /// - If tenant uses Sublicense mode: returns tenant's own sublicense (validated against platform)
    /// - For platform scope (tenantId = null): returns platform license
    /// </summary>
    Task<LicenseInfo?> GetEffectiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<LicenseValidationResult> InstallLicenseAsync(
        string licenseKey,
        Guid? tenantId = null,
        Guid? installedBy = null,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<LicenseValidationResult> ValidateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default);

    Task<bool> RevokeLicenseAsync(
        string reason,
        Guid? tenantId = null,
        Guid? revokedBy = null,
        CancellationToken cancellationToken = default);

    Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default);
}
