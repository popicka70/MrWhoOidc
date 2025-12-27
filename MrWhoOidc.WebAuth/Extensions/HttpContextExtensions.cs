using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
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

        // Canonical public base URL (preferred): stable across environments and reverse proxies.
        // When configured, we treat it as authoritative and will normalize any stored tenant issuer
        // (even if it was originally persisted with a different host like https://localhost:7157).
        var canonicalBaseUrl =
            (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? options.PublicBaseUrl.TrimEnd('/') : null)
            ?? (!string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/') : null);

        // Best-effort request base URL fallback.
        // NOTE: This can be wrong behind reverse proxies unless forwarded headers are configured.
        var requestBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

        // Base URL used to expand relative tenant issuers.
        var baseUrl = (canonicalBaseUrl ?? requestBaseUrl).TrimEnd('/');

        var tenantIssuer = tenantAccessor?.CurrentTenant?.IssuerUri;
        if (!string.IsNullOrWhiteSpace(tenantIssuer))
        {
            if (Uri.TryCreate(tenantIssuer, UriKind.Absolute, out var absTenantIssuer))
            {
                // If a canonical base URL is configured, prefer it as authoritative.
                // This makes deployments portable (e.g., dev DB restored into cloud) and fixes
                // cases where the stored issuer was derived from an internal binding like localhost.
                if (!string.IsNullOrWhiteSpace(canonicalBaseUrl) && Uri.TryCreate(canonicalBaseUrl, UriKind.Absolute, out var canonicalUri))
                {
                    // Extract the tenant path component from the stored issuer.
                    // For multi-tenant mode this should contain "/t/{slug}".
                    var tenantPath = absTenantIssuer.AbsolutePath ?? string.Empty;
                    var tIndex = tenantPath.IndexOf("/t/", StringComparison.OrdinalIgnoreCase);
                    if (tIndex > 0)
                    {
                        tenantPath = tenantPath.Substring(tIndex);
                    }

                    // If we couldn't find a tenant path, treat the tenant issuer as root.
                    if (string.IsNullOrWhiteSpace(tenantPath) || tenantPath == "/")
                    {
                        return canonicalBaseUrl.TrimEnd('/');
                    }

                    return (canonicalBaseUrl.TrimEnd('/') + tenantPath).TrimEnd('/');
                }

                return absTenantIssuer.ToString().TrimEnd('/');
            }

            // Some legacy code paths store tenant issuer as a relative path (e.g., "/t/default").
            // Normalize to an absolute issuer using the configured base URL.
            return (baseUrl + "/" + tenantIssuer.TrimStart('/')).TrimEnd('/');
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
            return baseUrl;
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
