using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing platform-wide settings that apply across all tenants.
/// </summary>
public interface IPlatformSettingsService
{
    /// <summary>
    /// Gets platform settings, creating default settings if none exist.
    /// Results are cached for performance.
    /// </summary>
    /// <returns>The platform settings (never null)</returns>
    Task<PlatformSettings> GetSettingsAsync();

    /// <summary>
    /// Updates platform settings and invalidates the cache.
    /// </summary>
    /// <param name="settings">The settings to save</param>
    /// <param name="updatedBy">Username or identifier of the admin making the change</param>
    Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy);

    /// <summary>
    /// Checks if QR login at the discovery page is enabled.
    /// This is a convenience method that reads from cached settings.
    /// </summary>
    /// <returns>True if QR login should be shown on /DiscoverTenant</returns>
    Task<bool> IsQrLoginAtDiscoveryEnabledAsync();
}
