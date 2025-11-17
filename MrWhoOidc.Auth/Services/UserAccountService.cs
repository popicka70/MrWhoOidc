using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserAccountService
{
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default);
}

internal sealed class UserAccountService(AuthDbContext dbContext) : IUserAccountService
{
    public async Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await dbContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);

    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        return await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == username, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        dbContext.UserAccounts.Add(account);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return account;
    }
}
