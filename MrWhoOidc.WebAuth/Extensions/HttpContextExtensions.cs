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
        // Prefer per-tenant issuer when available.
        // This avoids deriving issuer from request host/headers (which is error-prone and can be unsafe
        // if a deployment misconfigures proxy/host allow-lists).
        var tenantAccessor = httpContext.RequestServices.GetService<ITenantAccessor>();

        // Compute a best-effort base URL for cases where a tenant issuer is stored as a path.
        var baseUrl =
            (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? options.PublicBaseUrl.TrimEnd('/') : null)
            ?? (!string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/') : null)
            ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

        var tenantIssuer = tenantAccessor?.CurrentTenant?.IssuerUri;
        if (!string.IsNullOrWhiteSpace(tenantIssuer))
        {
            if (Uri.TryCreate(tenantIssuer, UriKind.Absolute, out var absTenantIssuer))
            {
                return absTenantIssuer.ToString().TrimEnd('/');
            }

            // Some legacy code paths store tenant issuer as a relative path (e.g., "/t/default").
            // Normalize to an absolute issuer using the configured base URL.
            return (baseUrl.TrimEnd('/') + "/" + tenantIssuer.TrimStart('/')).TrimEnd('/');
        }

        // If issuer is explicitly configured, use it (backward compatibility)
        if (!string.IsNullOrWhiteSpace(options.Issuer))
        {
            return options.Issuer.TrimEnd('/');
        }

        // Otherwise, use mode-aware issuer builder.
        // In multi-tenant mode this requires tenant context; if not yet resolved, fall back to base URL.
        var issuerBuilder = httpContext.RequestServices.GetRequiredService<IIssuerBuilder>();
        try
        {
            return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
        }
        catch (InvalidOperationException)
        {
            return baseUrl.TrimEnd('/');
        }
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
