namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Configuration options for multi-tenancy feature toggle.
/// The Enabled property is derived at runtime from the installed license, not from configuration.
/// </summary>
public interface IMultiTenancyOptions
{
    /// <summary>
    /// Whether multi-tenancy is enabled. 
    /// This value is determined by the installed platform license's deployment mode.
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
/// Note: The Enabled property is NOT used - multi-tenancy is controlled by license only.
/// Only DefaultTenantSlug is configurable via appsettings.
/// </summary>
public class MultiTenancyOptions : IMultiTenancyOptions
{
    /// <summary>
    /// This property is ignored. Multi-tenancy is controlled by the installed license.
    /// Use IMultiTenancyOptions.Enabled (from MultiTenancyStateProvider) for runtime checks.
    /// </summary>
    [Obsolete("Multi-tenancy is controlled by license, not configuration. Use IMultiTenancyOptions.Enabled instead.")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Slug of the default tenant. Configurable via appsettings.
    /// </summary>
    public string DefaultTenantSlug { get; set; } = "default";

    // Explicit interface implementation to avoid using the obsolete property
    bool IMultiTenancyOptions.Enabled => false; // Always false from config; real value from StateProvider
}
