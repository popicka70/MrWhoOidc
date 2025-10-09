using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Service for resolving tenant from request path.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Resolves tenant from the given path.
    /// Returns null if tenant cannot be resolved or doesn't exist.
    /// </summary>
    Task<TenantContext?> ResolveTenantAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mode-aware tenant resolver that handles both single-tenant and multi-tenant modes.
/// 
/// Single-tenant mode:
/// - Always returns the default tenant (configured by DefaultTenantSlug)
/// - Ignores path completely
/// - IssuerUri = base URL (no /t/{slug})
/// 
/// Multi-tenant mode:
/// - Parses path for /t/{slug} prefix
/// - Looks up tenant in database by slug
/// - IssuerUri = base URL + /t/{slug}
/// - Caches tenant lookups for performance
/// </summary>
public class ModeAwareTenantResolver : ITenantResolver
{
    private readonly AuthDbContext _dbContext;
    private readonly IMultiTenancyOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "tenant:";

    public ModeAwareTenantResolver(
        AuthDbContext dbContext,
        IMultiTenancyOptions options,
        IMemoryCache cache)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<TenantContext?> ResolveTenantAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        // Single-tenant mode: always return default tenant
        if (!_options.Enabled)
        {
            return await ResolveDefaultTenantAsync(cancellationToken);
        }

        // Multi-tenant mode: parse path for /t/{slug}
        var slug = ExtractTenantSlugFromPath(path);
        if (string.IsNullOrEmpty(slug))
        {
            // No tenant slug in path - fall back to default tenant for backward compatibility
            // This allows existing routes (e.g., /.well-known/openid-configuration) to work
            return await ResolveDefaultTenantAsync(cancellationToken);
        }

        // Path has /t/{slug} - look up the specific tenant
        var tenant = await ResolveTenantBySlugAsync(slug, cancellationToken);

        // If tenant not found and slug is NOT the default slug, return null (404)
        // If tenant not found but slug IS the default slug, still return null (config error - 500)
        return tenant;
    }

    /// <summary>
    /// Extracts tenant slug from path like "/t/acme/authorize" -> "acme"
    /// Returns null if path doesn't start with /t/
    /// </summary>
    private static string? ExtractTenantSlugFromPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return segments[1]; // tenant slug
    }

    private async Task<TenantContext?> ResolveDefaultTenantAsync(CancellationToken cancellationToken)
    {
        var slug = _options.DefaultTenantSlug;
        var cacheKey = $"{CacheKeyPrefix}default";

        if (_cache.TryGetValue<TenantContext>(cacheKey, out var cachedContext) && cachedContext != null)
        {
            return cachedContext;
        }

        var tenant = await _dbContext.Tenants
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant == null)
        {
            return null;
        }

        var context = new TenantContext
        {
            TenantId = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            IssuerUri = tenant.IssuerUri, // In single-tenant mode, this should be base URL
            IsMultiTenantMode = false
        };

        _cache.Set(cacheKey, context, CacheDuration);
        return context;
    }

    private async Task<TenantContext?> ResolveTenantBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        var cacheKey = $"{CacheKeyPrefix}{normalizedSlug}";

        if (_cache.TryGetValue<TenantContext>(cacheKey, out var cachedContext) && cachedContext != null)
        {
            return cachedContext;
        }

        // Case-insensitive comparison: fetch all active tenants and filter in memory
        // In-memory DB doesn't support ToLower in queries
        var tenants = await _dbContext.Tenants
            .Where(t => t.Status == TenantStatus.Active)
            .ToListAsync(cancellationToken);

        var tenant = tenants.FirstOrDefault(t =>
            t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

        if (tenant == null)
        {
            return null;
        }

        var context = new TenantContext
        {
            TenantId = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            IssuerUri = tenant.IssuerUri, // In multi-tenant mode, this includes /t/{slug}
            IsMultiTenantMode = true
        };

        _cache.Set(cacheKey, context, CacheDuration);
        return context;
    }
}
