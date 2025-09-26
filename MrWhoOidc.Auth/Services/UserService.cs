using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserService
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    // New: find by username OR primary/alternative email (case-insensitive for email)
    Task<User?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default);
    Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default);
}

internal sealed class UserService(AuthDbContext db, IPasswordHasher hasher) : IUserService
{
    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
        => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<User?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;

        // Fast path: direct username match (exact, case-sensitive as stored)
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == usernameOrEmail, ct);
        if (user != null) return user;

        // If looks like an email, normalize to lower and search primary + alternative verified emails.
        bool looksEmail = usernameOrEmail.Contains('@');
        if (!looksEmail) return null; // do not treat arbitrary strings as email for perf

        var email = usernameOrEmail.Trim().ToLowerInvariant();
        user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user != null) return user;

        // Alternative emails (only consider verified to reduce enumeration attacks)
        var alt = await db.UserAlternativeEmails.AsNoTracking()
            .Where(a => a.Email == email && a.IsVerified)
            .Select(a => a.UserId)
            .FirstOrDefaultAsync(ct);
        if (alt != Guid.Empty)
        {
            user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == alt, ct);
        }
        return user;
    }

    public Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default)
        => Task.FromResult(hasher.Verify(password, user.PasswordHash));
}
