using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserService
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default);
}

internal sealed class UserService(AuthDbContext db, IPasswordHasher hasher) : IUserService
{
    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
        => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default)
        => Task.FromResult(hasher.Verify(password, user.PasswordHash));
}
