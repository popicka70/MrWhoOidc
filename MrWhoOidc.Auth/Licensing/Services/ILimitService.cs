using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Services;

public interface ILimitService
{
    Task<long> GetLimitAsync(string limitType, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<bool> IsWithinLimitAsync(string limitType, long currentUsage, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageLimitInfo>> GetUsageLimitsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<bool> CanAddAsync(string limitType, long currentUsage, int additionalCount = 1, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
