using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for discovering tenants associated with a user's email address.
/// Implements caching and audit logging for security and performance.
/// </summary>
internal sealed class TenantDiscoveryService : ITenantDiscoveryService
{
    private readonly AuthDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantDiscoveryService> _logger;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    private readonly ITenantDomainClaimService _tenantDomainClaims;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "tenant_discovery:";

    public TenantDiscoveryService(
        AuthDbContext db,
        IMemoryCache cache,
        ILogger<TenantDiscoveryService> logger,
        IMultiTenancyOptions multiTenancyOptions,
        ITenantDomainClaimService tenantDomainClaims)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
        _multiTenancyOptions = multiTenancyOptions;
        _tenantDomainClaims = tenantDomainClaims;
    }

    public async Task<List<TenantInfo>> FindTenantsByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("FindTenantsByEmail called with null or empty email");
            return new List<TenantInfo>();
        }

        // Normalize email for lookup (lowercase, trim)
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            _logger.LogWarning("Email normalization failed for: {EmailHash}", HashEmail(email));
            return new List<TenantInfo>();
        }

        // Check cache first
        var cacheKey = $"{CacheKeyPrefix}{normalizedEmail}";
        if (_cache.TryGetValue<List<TenantInfo>>(cacheKey, out var cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("Cache hit for tenant discovery: {EmailHash}, found {Count} tenant(s)",
                HashEmail(email), cachedResult.Count);
            return cachedResult;
        }

        // Query database
        var tenants = await QueryTenantsAsync(normalizedEmail, ct);

        // Audit logging (use hashed email for privacy)
        _logger.LogInformation(
            "Tenant discovery: email={EmailHash}, tenants_found={Count}, cache=miss",
            HashEmail(email),
            tenants.Count);

        // Cache results
        _cache.Set(cacheKey, tenants, CacheDuration);

        return tenants;
    }

    public Task<TenantInfo?> GetPreferredTenantAsync(string email, string? ipAddress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult<TenantInfo?>(null);
        }

        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            return Task.FromResult<TenantInfo?>(null);
        }

        // For MVP, we don't store preferences in DB yet - will use cookies on client side
        // Future enhancement: query UserTenantPreference table by email + IP

        _logger.LogDebug("GetPreferredTenant called for {EmailHash} from IP {IP}",
            HashEmail(email), ipAddress ?? "unknown");

        return Task.FromResult<TenantInfo?>(null);
    }

    /// <summary>
    /// Query database for tenants associated with the given email.
    /// Searches both User.Email and UserAlternativeEmail.Email (verified only).
    /// </summary>
    private async Task<List<TenantInfo>> QueryTenantsAsync(string normalizedEmail, CancellationToken ct)
    {
        // Query 1: Find tenants via primary email (User.NormalizedEmail)
        var tenantsFromPrimary = await _db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Join(_db.Tenants,
                u => u.TenantId,
                t => t.Id,
                (u, t) => new { Tenant = t })
            .Where(x => x.Tenant.Status == TenantStatus.Active)
            .Select(x => new TenantInfo
            {
                TenantId = x.Tenant.Id,
                Slug = x.Tenant.Slug,
                Name = x.Tenant.Name,
                LogoUrl = x.Tenant.LogoUrl,
                TenantIconId = x.Tenant.TenantIconId,
                LoginUrl = _multiTenancyOptions.Enabled ? $"/t/{x.Tenant.Slug}/login" : "/login"
            })
            .Distinct()
            .ToListAsync(ct);

        // Query 2: Find tenants via alternative emails (only verified)
        var tenantsFromAlternative = await _db.UserAlternativeEmails
            .AsNoTracking()
            .Where(uae => uae.NormalizedEmail == normalizedEmail && uae.IsVerified)
            .Join(_db.Users,
                uae => uae.UserId,
                u => u.Id,
                (uae, u) => new { User = u })
            .Join(_db.Tenants,
                x => x.User.TenantId,
                t => t.Id,
                (x, t) => new { Tenant = t })
            .Where(x => x.Tenant.Status == TenantStatus.Active)
            .Select(x => new TenantInfo
            {
                TenantId = x.Tenant.Id,
                Slug = x.Tenant.Slug,
                Name = x.Tenant.Name,
                LogoUrl = x.Tenant.LogoUrl,
                TenantIconId = x.Tenant.TenantIconId,
                LoginUrl = _multiTenancyOptions.Enabled ? $"/t/{x.Tenant.Slug}/login" : "/login"
            })
            .Distinct()
            .ToListAsync(ct);

        // Combine and deduplicate by TenantId
        var allTenants = tenantsFromPrimary
            .Concat(tenantsFromAlternative)
            .GroupBy(t => t.TenantId)
            .Select(g => g.First())
            .OrderBy(t => t.Name)
            .ToList();

        var domainMatch = await _tenantDomainClaims.ResolveAutoJoinClaimAsync(normalizedEmail, ct).ConfigureAwait(false);
        if (domainMatch is not null && allTenants.All(t => t.TenantId != domainMatch.TenantId))
        {
            allTenants.Add(new TenantInfo
            {
                TenantId = domainMatch.TenantId,
                Slug = domainMatch.TenantSlug,
                Name = domainMatch.TenantName,
                LoginUrl = _multiTenancyOptions.Enabled ? $"/t/{domainMatch.TenantSlug}/login" : "/login"
            });
            allTenants = allTenants.OrderBy(t => t.Name).ToList();
        }

        return allTenants;
    }

    /// <summary>
    /// Hash email address for privacy in logs.
    /// Uses first 8 characters of SHA256 hash.
    /// </summary>
    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "empty";

        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(email.ToLowerInvariant())[..8].ToLowerInvariant();
    }
}
