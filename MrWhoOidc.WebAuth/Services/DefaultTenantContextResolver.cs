using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Provides unified logic for determining if the current request is operating 
/// within the default tenant context. Used by both page filters and layout rendering.
/// </summary>
public interface IDefaultTenantContextResolver
{
    /// <summary>
    /// Determines if the current HTTP context is in the default tenant context.
    /// </summary>
    /// <remarks>
    /// Returns true when:
    /// - Multi-tenancy is disabled (single tenant mode)
    /// - The current tenant slug matches the configured default tenant slug
    /// - No tenant slug can be determined (permissive fallback for first login scenarios)
    /// </remarks>
    bool IsDefaultTenantContext(HttpContext httpContext);
}

public class DefaultTenantContextResolver(
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantSwitchingService tenantSwitchingService) : IDefaultTenantContextResolver
{
    public bool IsDefaultTenantContext(HttpContext httpContext)
    {
        // Single-tenant mode is always "default" context
        if (!multiTenancyOptions.Enabled)
        {
            return true;
        }

        // Try to get tenant slug from middleware-resolved tenant first
        var slug = tenantAccessor.CurrentTenant?.Slug;

        // Fall back to session-stored preferred tenant if not resolved by middleware
        // (e.g., for routes that skip tenant resolution like /platform-admin/*)
        if (string.IsNullOrEmpty(slug))
        {
            slug = tenantSwitchingService.GetPreferredTenantSlug(httpContext);
        }

        // If no slug can be determined (e.g., first login, no session yet),
        // assume default tenant context to avoid blocking legitimate access.
        // This is permissive by design - actual authorization is enforced elsewhere.
        if (string.IsNullOrEmpty(slug))
        {
            return true;
        }

        return string.Equals(slug, multiTenancyOptions.DefaultTenantSlug, StringComparison.OrdinalIgnoreCase);
    }
}
