namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Represents the resolved tenant context for the current request.
/// In single-tenant mode, this will always contain the default tenant.
/// In multi-tenant mode, this is resolved from the request path.
/// </summary>
public class TenantContext
{
    /// <summary>
    /// Tenant ID (from database)
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Tenant slug (URL-safe identifier, e.g., "acme", "default")
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the tenant
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Computed issuer URI for this tenant.
    /// Single-tenant mode: https://auth.example.com
    /// Multi-tenant mode: https://auth.example.com/t/acme
    /// </summary>
    public string IssuerUri { get; set; } = string.Empty;

    /// <summary>
    /// Whether multi-tenancy is enabled (from configuration)
    /// </summary>
    public bool IsMultiTenantMode { get; set; }
}

/// <summary>
/// Service interface for accessing the current tenant context.
/// Registered as scoped service, populated by TenantResolutionMiddleware.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>
    /// Gets the current tenant context, or null if not yet resolved.
    /// </summary>
    TenantContext? CurrentTenant { get; }

    /// <summary>
    /// Sets the current tenant context (called by middleware).
    /// </summary>
    void SetTenant(TenantContext context);
}

/// <summary>
/// Default implementation of ITenantAccessor.
/// Stores tenant context in a scoped field.
/// </summary>
public class TenantAccessor : ITenantAccessor
{
    private TenantContext? _currentTenant;

    public TenantContext? CurrentTenant => _currentTenant;

    public void SetTenant(TenantContext context)
    {
        _currentTenant = context ?? throw new ArgumentNullException(nameof(context));
    }
}
