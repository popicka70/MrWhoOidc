using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantService
{
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default);
    Task<bool> CanProvisionTenantAsync(int additionalCount = 1, CancellationToken ct = default);
    Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, CancellationToken ct = default);
}

internal sealed class TenantService(AuthDbContext db, HybridCache cache, ILimitService limitService) : ITenantService
{
    private readonly AuthDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly HybridCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly ILimitService _limitService = limitService ?? throw new ArgumentNullException(nameof(limitService));

    public async Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var cacheKey = $"tenant:slug:{slug}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string>
        {
            "tenants"
        };

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await _db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug, cancel),
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"tenant:id:{tenantId}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string>
        {
            "tenants",
            $"tenant:{tenantId}"
        };

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await _db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancel),
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default)
    {
        var slugCacheKey = $"tenant:slug:{slug}";
        var idCacheKey = $"tenant:id:{tenantId}";
        
        await _cache.RemoveAsync(slugCacheKey, ct).ConfigureAwait(false);
        await _cache.RemoveAsync(idCacheKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> CanProvisionTenantAsync(int additionalCount = 1, CancellationToken ct = default)
    {
        if (additionalCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalCount), additionalCount, "Additional tenant count must be positive.");
        }

        var activeTenantCount = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return await _limitService.CanAddAsync(LicenseLimitTypes.Tenants, activeTenantCount, additionalCount, null, ct)
            .ConfigureAwait(false);
    }

    public async Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        // Check limits
        if (!await CanProvisionTenantAsync(1, ct))
        {
            throw new InvalidOperationException("Tenant limit reached.");
        }

        // Generate unique slug
        string slug;
        int attempts = 0;
        do
        {
            attempts++;
            if (attempts > 10) throw new InvalidOperationException("Failed to generate unique tenant slug.");
            
            // Generate 8 bytes -> ~11 chars in Base64Url
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            slug = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
        }
        while (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct));

        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            Status = TenantStatus.Active,
            IssuerUri = $"/t/{slug}", // This will be recomputed/fixed by middleware usually, but setting a default
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Tenants.Add(tenant);

        var membership = new UserTenantMembership
        {
            TenantId = tenant.Id,
            UserAccountId = creatorUserAccountId,
            IsTenantAdmin = true,
            Status = TenantMembershipStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.UserTenantMemberships.Add(membership);

        // Create User entity in the new tenant for the creator
        // This ensures the user can log in and access the dashboard immediately
        var userAccount = await _db.UserAccounts.FindAsync(new object[] { creatorUserAccountId }, ct);
        if (userAccount != null)
        {
            var user = new User
            {
                Id = userAccount.Id, // Keep same ID to match sub claim
                TenantId = tenant.Id,
                Username = userAccount.Username,
                Email = userAccount.Email,
                NormalizedEmail = userAccount.NormalizedEmail,
                Name = userAccount.Name,
                PasswordHash = userAccount.PasswordHash,
                PasswordSalt = userAccount.PasswordSalt,
                HashAlgorithm = userAccount.HashAlgorithm,
                EmailVerified = userAccount.EmailVerified,
                EmailVerifiedAt = userAccount.EmailVerifiedAt,
                CreatedAt = DateTimeOffset.UtcNow,
                TotpEnabled = userAccount.TotpEnabled,
                TotpSecret = userAccount.TotpSecret
            };
            _db.Users.Add(user);
        }

        await _db.SaveChangesAsync(ct);

        return tenant;
    }
}
