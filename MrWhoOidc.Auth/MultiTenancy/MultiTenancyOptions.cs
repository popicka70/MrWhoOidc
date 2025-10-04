namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Configuration options for multi-tenancy feature toggle.
/// </summary>
public interface IMultiTenancyOptions
{
    /// <summary>
    /// Whether multi-tenancy is enabled. 
    /// When false, system operates in single-tenant mode with all data belonging to default tenant.
    /// When true, system operates in multi-tenant mode with path-based tenant resolution.
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
/// </summary>
public class MultiTenancyOptions : IMultiTenancyOptions
{
    public bool Enabled { get; set; } = false; // Default to single-tenant mode
    public string DefaultTenantSlug { get; set; } = "default";
}
