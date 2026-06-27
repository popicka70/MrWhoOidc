using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantService
{
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidateTenantCacheAsync(Guid tenantId, string slug, CancellationToken ct = default);
    Task<bool> CanProvisionTenantAsync(int additionalCount = 1, CancellationToken ct = default);
    Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new tenant with an optional custom slug.
    /// </summary>
    /// <param name="name">The display name for the tenant.</param>
    /// <param name="creatorUserAccountId">The user account ID of the creator.</param>
    /// <param name="slug">Optional custom slug. If null, a unique slug will be generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created tenant.</returns>
    Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, string? slug, CancellationToken ct = default);

    /// <summary>
    /// Checks if the system is running in multi-tenant mode based on explicit configuration.
    /// </summary>
    bool IsMultiTenantMode { get; }
}

internal sealed class TenantService(
    AuthDbContext db,
    HybridCache cache,
    IMultiTenancyStateProvider stateProvider,
    IOptions<TenantCacheOptions> cacheOptions) : ITenantService
{
    private readonly AuthDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly HybridCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly IMultiTenancyStateProvider _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
    private readonly TenantCacheOptions _cacheOptions = cacheOptions?.Value ?? throw new ArgumentNullException(nameof(cacheOptions));

    /// <inheritdoc />
    public bool IsMultiTenantMode => _stateProvider.IsEnabled;

    public async Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var cacheKey = $"tenant:slug:{slug}";

        var options = new HybridCacheEntryOptions
        {
            Expiration = _cacheOptions.L2Expiration,
            LocalCacheExpiration = _cacheOptions.L1Expiration
        };

        var tags = new List<string>
        {
            TenantCacheOptions.CacheTag
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
            Expiration = _cacheOptions.L2Expiration,
            LocalCacheExpiration = _cacheOptions.L1Expiration
        };

        var tags = new List<string>
        {
            TenantCacheOptions.CacheTag,
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
        // Tenant provisioning is controlled by explicit multi-tenancy configuration.
        if (!IsMultiTenantMode)
        {
            return false;
        }

        if (additionalCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalCount), additionalCount, "Additional tenant count must be positive.");
        }

        await Task.CompletedTask;
        return true;
    }

    public async Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, CancellationToken ct = default)
    {
        return await CreateTenantAsync(name, creatorUserAccountId, slug: null, ct);
    }

    public async Task<Tenant> CreateTenantAsync(string name, Guid creatorUserAccountId, string? slug, CancellationToken ct = default)
    {
        // Tenant creation is controlled by explicit multi-tenancy configuration.
        if (!IsMultiTenantMode)
        {
            throw new InvalidOperationException("Cannot create tenants when multi-tenancy is disabled.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        // Check for duplicate tenant name (the unique index covers all rows, incl. soft-deleted).
        var nameTaken = await _db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Name == name, ct);
        if (nameTaken)
        {
            throw new InvalidOperationException($"A tenant with the name '{name}' already exists.");
        }

        // Check limits
        if (!await CanProvisionTenantAsync(1, ct))
        {
            throw new InvalidOperationException("Tenant limit reached.");
        }

        // Generate or validate slug
        if (string.IsNullOrWhiteSpace(slug))
        {
            // Generate unique slug
            slug = await GenerateUniqueSlugAsync(ct);
        }
        else
        {
            // Validate custom slug
            if (!TenantSlug.IsValid(slug))
            {
                throw new ArgumentException("Invalid slug format. Slugs must be 1-63 characters, lowercase letters, digits, and hyphens only.", nameof(slug));
            }

            // Check for duplicate slug (the unique index covers all rows, incl. soft-deleted).
            var existingSlug = await _db.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Slug == slug, ct);
            if (existingSlug)
            {
                throw new InvalidOperationException($"A tenant with the slug '{slug}' already exists.");
            }
        }

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
                EmailVerified = userAccount.EmailVerified,
                EmailVerifiedAt = userAccount.EmailVerifiedAt,
                CreatedAt = DateTimeOffset.UtcNow,
                TotpEnabled = userAccount.TotpEnabled,
                TotpSecret = userAccount.TotpSecret
            };
            _db.Users.Add(user);
        }

        _db.TenantAuditLogs.Add(new TenantAuditLog
        {
            TenantId = tenant.Id,
            Action = "Created",
            PerformedBy = creatorUserAccountId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return tenant;
    }

    private async Task<string> GenerateUniqueSlugAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // 8 random bytes -> 16 lowercase hex chars; always passes TenantSlug.IsValid.
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var candidate = Convert.ToHexString(bytes).ToLowerInvariant();

            var exists = await _db.Tenants.AnyAsync(t => t.Slug == candidate, ct);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to generate unique tenant slug.");
    }
}
