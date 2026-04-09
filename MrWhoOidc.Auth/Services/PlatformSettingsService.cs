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

        var cachedSettings = await _cache.GetOrCreateAsync(
            CacheKey,
            async cancel =>
            {
                // Use execution strategy to handle retries
                var strategy = _db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async ct =>
                {
                    var settings = await _db.PlatformSettings
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);

                    if (settings == null)
                    {
                        // Create default settings on first access
                        settings = CreateDefaultSettings();
                        _db.PlatformSettings.Add(settings);
                        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }

                    return CloneSettings(settings);
                }, cancel);
            },
            options,
            tags,
            CancellationToken.None
        ).ConfigureAwait(false);

        return CloneSettings(cachedSettings);
    }

    /// <inheritdoc />
    public async Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var currentSettings = await _db.PlatformSettings.FirstOrDefaultAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            if (currentSettings == null)
            {
                currentSettings = CreateDefaultSettings();
                currentSettings.CreatedAt = settings.CreatedAt == default ? now : settings.CreatedAt;
                _db.PlatformSettings.Add(currentSettings);
            }

            currentSettings.QrLoginAtDiscoveryEnabled = settings.QrLoginAtDiscoveryEnabled;
            currentSettings.DynamicClientRegistrationEnabled = settings.DynamicClientRegistrationEnabled;
            currentSettings.EnableTokenExchange = settings.EnableTokenExchange;
            currentSettings.UpdatedAt = now;
            currentSettings.UpdatedBy = updatedBy;

            await _db.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Invalidate cache so changes take effect immediately
        await _cache.RemoveAsync(CacheKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsQrLoginAtDiscoveryEnabledAsync()
    {
        var settings = await GetSettingsAsync();
        return settings.QrLoginAtDiscoveryEnabled;
    }

    private PlatformSettings CreateDefaultSettings()
        => new()
        {
            DynamicClientRegistrationEnabled = _authOptions.Value.EnableDynamicClientRegistration,
            EnableTokenExchange = _authOptions.Value.EnableTokenExchange
        };

    private static PlatformSettings CloneSettings(PlatformSettings settings)
        => new()
        {
            Id = settings.Id,
            QrLoginAtDiscoveryEnabled = settings.QrLoginAtDiscoveryEnabled,
            DynamicClientRegistrationEnabled = settings.DynamicClientRegistrationEnabled,
            EnableTokenExchange = settings.EnableTokenExchange,
            CreatedAt = settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy
        };
}
