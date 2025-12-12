using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.AspNetCore.Http.Extensions;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for HttpContext.
/// </summary>
public static class HttpContextExtensions
{
    public static string GetIssuer(this HttpContext httpContext)
    {
        var options = httpContext.RequestServices.GetService<OidcOptions>() ?? new OidcOptions();
        return httpContext.GetIssuer(options);
    }

    /// <summary>
    /// Gets the issuer URL for the current request.
    /// In single-tenant mode: returns root issuer (e.g., https://auth.example.com)
    /// In multi-tenant mode: returns path-based issuer (e.g., https://auth.example.com/t/tenant-slug)
    /// Falls back to configured issuer in OidcOptions if available.
    /// </summary>
    /// <param name="httpContext">The HTTP context.</param>
    /// <param name="options">The OIDC options containing the configured issuer.</param>
    /// <returns>The issuer URL.</returns>
    public static string GetIssuer(this HttpContext httpContext, OidcOptions options)
    {
        // If issuer is explicitly configured, use it (backward compatibility)
        if (!string.IsNullOrEmpty(options.Issuer))
        {
            return options.Issuer.TrimEnd('/');
        }

        // Use PublicBaseUrl if configured (for Docker/proxy scenarios), otherwise use request URL
        var baseUrl = !string.IsNullOrEmpty(options.PublicBaseUrl)
            ? options.PublicBaseUrl.TrimEnd('/')
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

        // Use mode-aware issuer builder to construct tenant-specific issuer
        var issuerBuilder = httpContext.RequestServices.GetRequiredService<IIssuerBuilder>();
        return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
    }

    public static string GetEndpointUrl(this HttpContext httpContext)
    {
        return UriHelper.BuildAbsolute(
            httpContext.Request.Scheme,
            httpContext.Request.Host,
            httpContext.Request.PathBase,
            httpContext.Request.Path);
    }
}
