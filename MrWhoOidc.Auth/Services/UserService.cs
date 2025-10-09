using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IUserService
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    // New: find by username OR primary/alternative email (case-insensitive for email)
    Task<User?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default);
    Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default);
}

internal sealed class UserService(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor) : IUserService
{
    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().Where(u => u.Username == username);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(u => u.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        return query.FirstOrDefaultAsync(ct);
    }

    public async Task<User?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;

        // Fast path: direct username match (exact, case-sensitive as stored)
        var query = db.Users.AsNoTracking().Where(u => u.Username == usernameOrEmail);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            query = query.Where(u => u.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        var user = await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (user != null) return user;

        // If looks like an email, normalize to lower and search primary + alternative verified emails.
        bool looksEmail = usernameOrEmail.Contains('@');
        if (!looksEmail) return null; // do not treat arbitrary strings as email for perf

        var email = EmailNormalizer.NormalizeForLookup(usernameOrEmail);
        if (string.IsNullOrEmpty(email)) return null;

        var emailQuery = db.Users.AsNoTracking().Where(u => u.NormalizedEmail == email);

        // Filter by tenant if tenant context is available
        if (tenantAccessor.CurrentTenant != null)
        {
            emailQuery = emailQuery.Where(u => u.TenantId == tenantAccessor.CurrentTenant.TenantId);
        }

        user = await emailQuery.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (user != null) return user;

        // Alternative emails (only consider verified to reduce enumeration attacks)
        var altQuery = db.UserAlternativeEmails.AsNoTracking()
            .Where(a => a.NormalizedEmail == email && a.IsVerified);

        // Join with Users to filter by tenant
        if (tenantAccessor.CurrentTenant != null)
        {
            altQuery = altQuery.Where(a => db.Users.Any(u => u.Id == a.UserId && u.TenantId == tenantAccessor.CurrentTenant.TenantId));
        }

        var alt = await altQuery
            .Select(a => a.UserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (alt != Guid.Empty)
        {
            user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == alt, ct).ConfigureAwait(false);
        }
        return user;
    }

    public Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default)
        => Task.FromResult(hasher.Verify(password, user.PasswordHash));
}
