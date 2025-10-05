using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for HttpContext.
/// </summary>
public static class HttpContextExtensions
{
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
            return options.Issuer;
        }

        // Otherwise, use mode-aware issuer builder
        var issuerBuilder = httpContext.RequestServices.GetRequiredService<IIssuerBuilder>();
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        return issuerBuilder.BuildIssuer(baseUrl);
    }
}
