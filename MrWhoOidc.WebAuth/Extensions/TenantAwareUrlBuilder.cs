using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for building tenant-aware URLs throughout the application.
/// Centralizes the logic for determining whether to use tenant-prefixed paths based on multi-tenancy configuration.
/// </summary>
public static class TenantAwareUrlBuilder
{
    /// <summary>
    /// Builds a tenant-aware URL path.
    /// In single-tenant mode: returns the path as-is (e.g., "/Admin/Clients")
    /// In multi-tenant mode: prepends tenant prefix (e.g., "/t/acme/Admin/Clients")
    /// </summary>
    /// <param name="path">The path to build (must start with /)</param>
    /// <param name="tenantAccessor">Tenant accessor for getting current tenant</param>
    /// <param name="multiTenancyOptions">Multi-tenancy options to check if enabled</param>
    /// <returns>The complete tenant-aware URL path</returns>
    public static string BuildTenantPath(
        string path,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }
        
        // Ensure path starts with /
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // Only add tenant prefix if multi-tenancy is enabled and we have a current tenant
        var currentTenant = tenantAccessor.CurrentTenant;
        if (multiTenancyOptions.Enabled && currentTenant != null)
        {
            return $"/t/{currentTenant.Slug}{path}";
        }

        return path;
    }

    /// <summary>
    /// Builds a tenant-aware URL path with query string parameters.
    /// </summary>
    /// <param name="path">The path to build (must start with /)</param>
    /// <param name="tenantAccessor">Tenant accessor for getting current tenant</param>
    /// <param name="multiTenancyOptions">Multi-tenancy options to check if enabled</param>
    /// <param name="queryParams">Optional query string parameters as key-value pairs</param>
    /// <returns>The complete tenant-aware URL path with query string</returns>
    public static string BuildTenantPath(
        string path,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        params (string key, string? value)[] queryParams)
    {
        var url = BuildTenantPath(path, tenantAccessor, multiTenancyOptions);

        var validParams = queryParams
            .Where(p => !string.IsNullOrEmpty(p.value))
            .Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value!)}")
            .ToArray();

        if (validParams.Length > 0)
        {
            url += "?" + string.Join("&", validParams);
        }

        return url;
    }

    /// <summary>
    /// Extension method for HttpContext to build tenant-aware URLs.
    /// </summary>
    public static string BuildTenantUrl(
        this HttpContext httpContext,
        string path,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions)
    {
        return BuildTenantPath(path, tenantAccessor, multiTenancyOptions);
    }

    /// <summary>
    /// Extension method for HttpContext to build tenant-aware URLs with query parameters.
    /// </summary>
    public static string BuildTenantUrl(
        this HttpContext httpContext,
        string path,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        params (string key, string? value)[] queryParams)
    {
        return BuildTenantPath(path, tenantAccessor, multiTenancyOptions, queryParams);
    }
}
