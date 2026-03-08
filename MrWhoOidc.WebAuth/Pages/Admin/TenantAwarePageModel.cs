using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Extensions;

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
    /// Gets the multi-tenancy options for checking if multi-tenancy is enabled.
    /// </summary>
    protected IMultiTenancyOptions MultiTenancyOptions => _multiTenancyOptions;

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
        string url;

        if (routeValues != null)
        {
            // Extract query parameters from route values object
            var properties = routeValues.GetType().GetProperties();
            var queryParams = properties
                .Select(prop => (prop.Name, prop.GetValue(routeValues)?.ToString()))
                .ToArray();

            url = TenantAwareUrlBuilder.BuildTenantPath(
                pagePath,
                _tenantAccessor,
                _multiTenancyOptions,
                queryParams);
        }
        else
        {
            url = TenantAwareUrlBuilder.BuildTenantPath(
                pagePath,
                _tenantAccessor,
                _multiTenancyOptions);
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

        // Build tenant-aware URL using the helper
        var url = TenantAwareUrlBuilder.BuildTenantPath(
            currentPath,
            _tenantAccessor,
            _multiTenancyOptions);

        return Redirect(url);
    }
}
