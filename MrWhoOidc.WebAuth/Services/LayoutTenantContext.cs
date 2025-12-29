using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Provides unified tenant context for layout rendering.
/// Unlike <see cref="ITenantAccessor"/> which is populated by middleware based on URL path,
/// this service ALWAYS resolves tenant context using the following priority:
/// 1. Middleware-resolved tenant (from URL path /t/{slug}/...)
/// 2. Session-stored preferred tenant (from tenant switcher)
/// 3. Default tenant (as configured in multi-tenancy options)
/// 
/// This ensures consistent menu visibility and link generation even on pages
/// that skip tenant resolution middleware (e.g., /platform-admin/*).
/// </summary>
public interface ILayoutTenantContextService
{
    /// <summary>
    /// Gets the tenant context for layout rendering. Never returns null in valid configurations.
    /// </summary>
    Task<LayoutTenantContext> GetLayoutTenantContextAsync(HttpContext httpContext, CancellationToken ct = default);
}

/// <summary>
/// Tenant context information needed for layout rendering.
/// </summary>
public sealed class LayoutTenantContext
{
    /// <summary>
    /// The resolved tenant ID.
    /// </summary>
    public Guid TenantId { get; init; }
    
    /// <summary>
    /// The tenant slug used in URLs.
    /// </summary>
    public string Slug { get; init; } = string.Empty;
    
    /// <summary>
    /// The tenant display name.
    /// </summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>
    /// The URL prefix for tenant-scoped links (e.g., "/t/default" or "" for single-tenant).
    /// </summary>
    public string UrlPrefix { get; init; } = string.Empty;
    
    /// <summary>
    /// Whether multi-tenancy is enabled.
    /// </summary>
    public bool IsMultiTenantMode { get; init; }
    
    /// <summary>
    /// Source of the tenant resolution: "url", "session", "default", or "none".
    /// </summary>
    public string ResolutionSource { get; init; } = "none";
}

public sealed class LayoutTenantContextService(
    ITenantAccessor tenantAccessor,
    ITenantSwitchingService tenantSwitchingService,
    IMultiTenancyOptions multiTenancyOptions,
    AuthDbContext db) : ILayoutTenantContextService
{
    public async Task<LayoutTenantContext> GetLayoutTenantContextAsync(HttpContext httpContext, CancellationToken ct = default)
    {
        // Single-tenant mode: return empty prefix
        if (!multiTenancyOptions.Enabled)
        {
            // Still try to get default tenant info for name display
            var defaultTenant = await db.Tenants.AsNoTracking()
                .Where(t => t.Slug == multiTenancyOptions.DefaultTenantSlug)
                .Select(t => new { t.Id, t.Slug, t.Name })
                .FirstOrDefaultAsync(ct);
            
            return new LayoutTenantContext
            {
                TenantId = defaultTenant?.Id ?? Guid.Empty,
                Slug = defaultTenant?.Slug ?? "",
                Name = defaultTenant?.Name ?? "Default",
                UrlPrefix = "", // No prefix in single-tenant mode
                IsMultiTenantMode = false,
                ResolutionSource = "default"
            };
        }

        // Priority 1: Check if middleware already resolved tenant (from URL path)
        var middlewareTenant = tenantAccessor.CurrentTenant;
        if (middlewareTenant != null)
        {
            return new LayoutTenantContext
            {
                TenantId = middlewareTenant.TenantId,
                Slug = middlewareTenant.Slug,
                Name = middlewareTenant.Name,
                UrlPrefix = $"/t/{middlewareTenant.Slug}",
                IsMultiTenantMode = true,
                ResolutionSource = "url"
            };
        }

        // Priority 2: Check session for preferred tenant (set by tenant switcher)
        var sessionSlug = tenantSwitchingService.GetPreferredTenantSlug(httpContext);
        if (!string.IsNullOrEmpty(sessionSlug))
        {
            var sessionTenant = await db.Tenants.AsNoTracking()
                .Where(t => t.Slug == sessionSlug && t.Status == TenantStatus.Active)
                .Select(t => new { t.Id, t.Slug, t.Name })
                .FirstOrDefaultAsync(ct);
            
            if (sessionTenant != null)
            {
                return new LayoutTenantContext
                {
                    TenantId = sessionTenant.Id,
                    Slug = sessionTenant.Slug,
                    Name = sessionTenant.Name,
                    UrlPrefix = $"/t/{sessionTenant.Slug}",
                    IsMultiTenantMode = true,
                    ResolutionSource = "session"
                };
            }
        }

        // Priority 3: Fall back to default tenant
        var fallbackTenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Slug == multiTenancyOptions.DefaultTenantSlug && t.Status == TenantStatus.Active)
            .Select(t => new { t.Id, t.Slug, t.Name })
            .FirstOrDefaultAsync(ct);
        
        if (fallbackTenant != null)
        {
            return new LayoutTenantContext
            {
                TenantId = fallbackTenant.Id,
                Slug = fallbackTenant.Slug,
                Name = fallbackTenant.Name,
                UrlPrefix = $"/t/{fallbackTenant.Slug}",
                IsMultiTenantMode = true,
                ResolutionSource = "default"
            };
        }

        // Last resort: no tenant found (configuration error)
        return new LayoutTenantContext
        {
            TenantId = Guid.Empty,
            Slug = "",
            Name = "Unknown",
            UrlPrefix = "",
            IsMultiTenantMode = true,
            ResolutionSource = "none"
        };
    }
}
