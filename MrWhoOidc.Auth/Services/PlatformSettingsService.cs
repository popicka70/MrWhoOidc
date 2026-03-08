using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Implementation of platform settings service with HybridCache for performance.
/// Uses same caching pattern as TenantSettingsService.
/// </summary>
public class PlatformSettingsService : IPlatformSettingsService
{
    private readonly AuthDbContext _db;
    private readonly HybridCache _cache;
    private readonly IOptions<AuthOptions> _authOptions;
    private const string CacheKey = "platform:settings";

    public PlatformSettingsService(AuthDbContext db, HybridCache cache, IOptions<AuthOptions> authOptions)
    {
        _db = db;
        _cache = cache;
        _authOptions = authOptions;
    }

    /// <inheritdoc />
    public async Task<PlatformSettings> GetSettingsAsync()
    {
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(1),             // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
        };

        var tags = new List<string> { "platform-settings" };

        return await _cache.GetOrCreateAsync(
            CacheKey,
            async cancel =>
            {
                // Use execution strategy to handle retries
                var strategy = _db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async ct =>
                {
                    var settings = await _db.PlatformSettings.FirstOrDefaultAsync(ct);
                    
                    if (settings == null)
                    {
                        // Create default settings on first access
                        settings = new PlatformSettings
                        {
                            EnableTokenExchange = _authOptions.Value.EnableTokenExchange
                        };
                        _db.PlatformSettings.Add(settings);
                        await _db.SaveChangesAsync(ct);
                    }
                    
                    return settings;
                }, cancel);
            },
            options,
            tags,
            CancellationToken.None
        ).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = updatedBy;
        
        // Check if this entity or another entity with same key is already being tracked
        var trackedEntity = _db.ChangeTracker.Entries<PlatformSettings>()
            .FirstOrDefault(e => e.Entity.Id == settings.Id);
        
        if (trackedEntity != null)
        {
            // If different instance with same key is tracked, detach it first
            if (!ReferenceEquals(trackedEntity.Entity, settings))
            {
                trackedEntity.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
        
        // Now attach/update the settings entity
        var entry = _db.Entry(settings);
        if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Detached)
        {
            _db.PlatformSettings.Update(settings);
        }
        else
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        }
        
        await _db.SaveChangesAsync();
        
        // Invalidate cache so changes take effect immediately
        await _cache.RemoveAsync(CacheKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsQrLoginAtDiscoveryEnabledAsync()
    {
        var settings = await GetSettingsAsync();
        return settings.QrLoginAtDiscoveryEnabled;
    }
}
