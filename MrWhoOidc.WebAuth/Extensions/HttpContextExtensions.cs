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

        var multiTenancyOptions = httpContext.RequestServices.GetService<IMultiTenancyOptions>();
        // Only resolve IIssuerBuilder when multi-tenancy is enabled to avoid unnecessary requirements for single-tenant scenarios (e.g., in tests)
        var issuerBuilder = (multiTenancyOptions?.Enabled ?? false) ? httpContext.RequestServices.GetRequiredService<IIssuerBuilder>() : null;

        // Single-tenant mode: never emit tenant-prefixed issuers (e.g., "/t/default") even if
        // tenant records contain legacy issuer values. We still accept tenant-prefixed routes for
        // compatibility, but the canonical issuer should remain the root authority.
        if (!(multiTenancyOptions?.Enabled ?? false))
        {
            if (!string.IsNullOrWhiteSpace(options.Issuer))
            {
                return options.Issuer.TrimEnd('/');
            }

            if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            {
                return options.PublicBaseUrl.TrimEnd('/');
            }

            return baseUrl;
        }

        var tenantIssuer = tenantAccessor?.CurrentTenant?.IssuerUri;
        if (!string.IsNullOrWhiteSpace(tenantIssuer))
        {
            if (Uri.TryCreate(tenantIssuer, UriKind.Absolute, out var absTenantIssuer))
            {
                // Back-compat: some persisted issuers may be root-only even when multi-tenancy is enabled.
                // In that case, prefer the mode-aware issuer builder so discovery and protocol endpoints
                // remain tenant-scoped (e.g., https://host/t/{slug}).
                var tenantPathCandidate = absTenantIssuer.AbsolutePath ?? string.Empty;
                var hasTenantPath = tenantPathCandidate.IndexOf("/t/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasTenantPath)
                {
                    return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
                }

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
                        return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
                    }

                    return (canonicalBaseUrl.TrimEnd('/') + tenantPath).TrimEnd('/');
                }

                return absTenantIssuer.ToString().TrimEnd('/');
            }

            // Some legacy code paths store tenant issuer as a relative path (e.g., "/t/default").
            // Normalize to an absolute issuer using the configured base URL.
            // If the stored issuer is relative but doesn't include /t/{slug} in multi-tenant mode,
            // prefer the mode-aware issuer builder.
            var normalizedRelative = ("/" + tenantIssuer.TrimStart('/')).Replace("//", "/", StringComparison.Ordinal);
            if (normalizedRelative.IndexOf("/t/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
            }

            return (baseUrl + "/" + tenantIssuer.TrimStart('/')).TrimEnd('/');
        }

        // If issuer is explicitly configured, use it (backward compatibility)
        if (!string.IsNullOrWhiteSpace(options.Issuer))
        {
            // In multi-tenant mode, the configured issuer acts as the canonical base URL.
            // Emit a tenant-scoped issuer when tenant context is available.
            if (tenantAccessor?.CurrentTenant is not null)
            {
                return issuerBuilder.BuildIssuer(baseUrl).TrimEnd('/');
            }

            return options.Issuer.TrimEnd('/');
        }

        // Otherwise, use mode-aware issuer builder.
        // In multi-tenant mode this requires tenant context; if not yet resolved, fall back to base URL.
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

    public static string GetCorrelationId(this HttpContext httpContext)
    {
        return System.Diagnostics.Activity.Current?.Id 
            ?? httpContext.Items["CorrelationId"] as string 
            ?? Guid.NewGuid().ToString("N");
    }

    public static Guid? GetTenantId(this HttpContext httpContext)
    {
        var tenantAccessor = httpContext.RequestServices.GetService<ITenantAccessor>();
        return tenantAccessor?.CurrentTenant?.TenantId;
    }
}
