using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin;

/// <summary>
/// Base page model for admin pages that provides tenant-aware redirect helpers.
/// </summary>
public abstract class TenantAwarePageModel : PageModel
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMultiTenancyOptions _multiTenancyOptions;

    protected TenantAwarePageModel(ITenantAccessor tenantAccessor, IMultiTenancyOptions multiTenancyOptions)
    {
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _multiTenancyOptions = multiTenancyOptions ?? throw new ArgumentNullException(nameof(multiTenancyOptions));
    }

    /// <summary>
    /// Gets the tenant accessor for accessing current tenant information.
    /// </summary>
    protected ITenantAccessor TenantAccessor => _tenantAccessor;

    /// <summary>
    /// Redirects to a page within the current tenant context.
    /// In single-tenant mode: redirects to root-level path
    /// In multi-tenant mode: redirects to tenant-prefixed path
    /// </summary>
    /// <param name="pagePath">The page path (e.g., "/Admin/Users/Index" or "Index")</param>
    /// <param name="routeValues">Optional route values to append as query string</param>
    /// <returns>A redirect result with tenant-aware URL</returns>
    protected IActionResult TenantAwareRedirect(string pagePath, object? routeValues = null)
    {
        var currentTenant = _tenantAccessor.CurrentTenant;
        
        // Ensure page path starts with /
        if (!pagePath.StartsWith('/'))
        {
            pagePath = "/" + pagePath;
        }

        // Build tenant-aware URL only if multi-tenancy is enabled
        var url = (_multiTenancyOptions.Enabled && currentTenant != null)
            ? $"/t/{currentTenant.Slug}{pagePath}"
            : pagePath;

        // Append route values as query string
        if (routeValues != null)
        {
            var queryParams = new List<string>();
            var properties = routeValues.GetType().GetProperties();
            
            foreach (var prop in properties)
            {
                var value = prop.GetValue(routeValues);
                if (value != null)
                {
                    queryParams.Add($"{prop.Name}={Uri.EscapeDataString(value.ToString()!)}");
                }
            }

            if (queryParams.Any())
            {
                url += "?" + string.Join("&", queryParams);
            }
        }

        return Redirect(url);
    }

    /// <summary>
    /// Redirects to the current page (useful for POST-Redirect-GET pattern).
    /// In single-tenant mode: redirects to root-level path
    /// In multi-tenant mode: redirects to tenant-prefixed path
    /// </summary>
    protected IActionResult TenantAwareRedirectToPage()
    {
        var currentPath = HttpContext.Request.Path.Value ?? "/";
        
        // If already has tenant prefix, just redirect to current path
        if (currentPath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(currentPath);
        }

        // Otherwise, build tenant-aware URL only if multi-tenancy is enabled
        var currentTenant = _tenantAccessor.CurrentTenant;
        var url = (_multiTenancyOptions.Enabled && currentTenant != null)
            ? $"/t/{currentTenant.Slug}{currentPath}"
            : currentPath;

        return Redirect(url);
    }
}
