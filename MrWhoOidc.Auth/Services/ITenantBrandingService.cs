using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for accessing tenant branding configuration.
/// </summary>
public interface ITenantBrandingService
{
    /// <summary>
    /// Gets the branding configuration for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <returns>Branding configuration, or null if not found</returns>
    Task<TenantBranding?> GetBrandingAsync(Guid tenantId);
    
    /// <summary>
    /// Gets the branding configuration for the current tenant from context.
    /// </summary>
    /// <returns>Branding configuration, or default branding if not found</returns>
    Task<TenantBranding> GetCurrentTenantBrandingAsync();
}

/// <summary>
/// Represents tenant branding configuration.
/// </summary>
public class TenantBranding
{
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string TenantName { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets the primary color or a default if not set.
    /// </summary>
    public string GetPrimaryColorOrDefault() => PrimaryColor ?? "#007bff";
    
    /// <summary>
    /// Gets the accent color or a default if not set.
    /// </summary>
    public string GetAccentColorOrDefault() => AccentColor ?? "#6c757d";
    
    /// <summary>
    /// Gets whether custom branding is configured.
    /// </summary>
    public bool HasCustomBranding => 
        !string.IsNullOrEmpty(LogoUrl) || 
        !string.IsNullOrEmpty(PrimaryColor) || 
        !string.IsNullOrEmpty(AccentColor);
}
