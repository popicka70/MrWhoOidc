using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IUserService
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    // New: find by username OR primary/alternative email (case-insensitive for email)
    Task<User?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default);
    Task<User?> FindByIdAcrossTenantsAsync(Guid userId, CancellationToken ct = default);
    
    /// <summary>
    /// Verifies password against the per-tenant User.PasswordHash.
    /// </summary>
    /// <remarks>
    /// <b>DEPRECATION NOTICE</b>: Use <see cref="IGlobalAuthenticationService.AuthenticateAsync"/> instead,
    /// which verifies against the global <c>UserAccount.PasswordHash</c>.
    /// This method is retained for migration compatibility.
    /// </remarks>
    [Obsolete("Use IGlobalAuthenticationService.AuthenticateAsync for authentication.")]
    Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default);
    
    /// <summary>
    /// Invalidates cached user data for the specified user.
    /// Call this after user updates (profile, password, email, MFA, etc.).
    /// </summary>
    Task InvalidateUserCacheAsync(Guid userId, string username, Guid tenantId, CancellationToken ct = default);
}

internal sealed class UserService(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor, HybridCache cache) : IUserService
{
    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
        var cacheKey = $"user:username:{tenantId}:{username}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10),         // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(2) // L1 (memory) - shorter for security
        };

        var tags = new List<string>
        {
            "users",
            $"tenant:{tenantId}"
        };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                var query = db.Users.AsNoTracking().Where(u => u.Username == username);

                // Filter by tenant if tenant context is available
                if (tenantAccessor.CurrentTenant != null)
                {
                    query = query.Where(u => u.TenantId == tenantAccessor.CurrentTenant.TenantId);
                }

                return await query.FirstOrDefaultAsync(cancel);
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
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

    public async Task<User?> FindByIdAcrossTenantsAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
    }

    public Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default)
        => Task.FromResult(hasher.Verify(password, user.PasswordHash));

    public async Task InvalidateUserCacheAsync(Guid userId, string username, Guid tenantId, CancellationToken ct = default)
    {
        // Invalidate username-based cache
        var usernameCacheKey = $"user:username:{tenantId}:{username}";
        await cache.RemoveAsync(usernameCacheKey, ct).ConfigureAwait(false);

        // Optionally: Use tag-based invalidation if needed for email lookups
        // Note: FindByUsernameOrEmailAsync is not cached due to multiple query paths
        // Consider adding separate email-based cache if email lookups become frequent
    }
}
