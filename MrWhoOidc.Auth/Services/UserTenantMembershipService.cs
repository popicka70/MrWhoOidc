using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserTenantMembershipService
{
    Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userAccountId, CancellationToken ct = default);
    Task<UserTenantMembership?> GetMembershipAsync(Guid userAccountId, Guid tenantId, CancellationToken ct = default);
    Task<UserTenantMembership> CreateAsync(UserTenantMembership membership, CancellationToken ct = default);
    Task<IReadOnlyList<UserTenantMembership>> GetMembershipsByUsernameAsync(string username, CancellationToken ct = default);
}

internal sealed class UserTenantMembershipService(AuthDbContext dbContext) : IUserTenantMembershipService
{
    public async Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userAccountId, CancellationToken ct = default)
    {
        return await dbContext.UserTenantMemberships.AsNoTracking()
            .Where(x => x.UserAccountId == userAccountId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<UserTenantMembership?> GetMembershipAsync(Guid userAccountId, Guid tenantId, CancellationToken ct = default)
    {
        return await dbContext.UserTenantMemberships.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserAccountId == userAccountId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserTenantMembership> CreateAsync(UserTenantMembership membership, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(membership);
        dbContext.UserTenantMemberships.Add(membership);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return membership;
    }

    public async Task<IReadOnlyList<UserTenantMembership>> GetMembershipsByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Array.Empty<UserTenantMembership>();
        }

        return await dbContext.UserTenantMemberships.AsNoTracking()
            .Where(x => x.UserAccount.Username == username)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
