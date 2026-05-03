namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Configuration options for multi-tenancy feature toggle.
/// The Enabled property is controlled explicitly from configuration.
/// </summary>
public interface IMultiTenancyOptions
{
    /// <summary>
    /// Whether multi-tenancy is enabled. 
    /// When false, the system operates in single-tenant mode with all data belonging to the default tenant.
    /// When true, the system operates in multi-tenant mode with path-based tenant resolution.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Slug of the default tenant used in single-tenant mode or as fallback.
    /// Default: "default"
    /// </summary>
    string DefaultTenantSlug { get; }
}

/// <summary>
/// Multi-tenancy configuration options.
/// Both Enabled and DefaultTenantSlug are configurable via appsettings.
/// </summary>
public class MultiTenancyOptions : IMultiTenancyOptions
{
    /// <summary>
    /// Whether multi-tenancy is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Slug of the default tenant. Configurable via appsettings.
    /// </summary>
    public string DefaultTenantSlug { get; set; } = "default";
}
