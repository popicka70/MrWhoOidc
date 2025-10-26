using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Repositories;

public interface ILicenseRepository
{
    Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default);

    Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default);

    Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default);

    Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default);

    Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default);
}
