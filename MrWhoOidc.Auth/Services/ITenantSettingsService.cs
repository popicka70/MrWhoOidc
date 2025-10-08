using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing cascading settings (Platform → Tenant → Client).
/// </summary>
public interface ITenantSettingsService
{
    /// <summary>
    /// Gets the effective settings for a specific tenant, with platform defaults as fallback.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <returns>Resolved tenant settings, or null if tenant not found</returns>
    Task<TenantSettings?> GetTenantSettingsAsync(Guid tenantId);
    
    /// <summary>
    /// Gets the effective settings for the current tenant from context.
    /// </summary>
    /// <returns>Resolved tenant settings, or platform defaults if no tenant context</returns>
    Task<TenantSettings> GetCurrentTenantSettingsAsync();
    
    /// <summary>
    /// Updates the settings JSON for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="settings">The settings to save</param>
    /// <returns>True if successful, false if tenant not found</returns>
    Task<bool> UpdateTenantSettingsAsync(Guid tenantId, TenantSettings settings);
    
    /// <summary>
    /// Gets platform default settings (from appsettings.json).
    /// </summary>
    /// <returns>Platform default settings</returns>
    TenantSettings GetPlatformDefaults();
}

/// <summary>
/// Result of settings resolution showing which values came from which source.
/// </summary>
public class ResolvedSettings
{
    public TenantSettings Settings { get; set; } = new();
    public Dictionary<string, string> Sources { get; set; } = new();
    
    /// <summary>
    /// Adds a source annotation for a setting path.
    /// </summary>
    /// <param name="path">Setting path (e.g., "oidc.requirePkce")</param>
    /// <param name="source">Source (e.g., "platform", "tenant", "client")</param>
    public void AddSource(string path, string source)
    {
        Sources[path] = source;
    }
}
