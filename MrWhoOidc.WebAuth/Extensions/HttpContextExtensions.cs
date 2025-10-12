using Microsoft.Extensions.Logging;
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
        var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("MrWhoOidc.GetIssuer");
        
        logger.LogInformation("🔍 [GetIssuer] Called from {Path}, Scheme={Scheme}, Host={Host}", 
            httpContext.Request.Path, 
            httpContext.Request.Scheme, 
            httpContext.Request.Host);
        
        // If issuer is explicitly configured, use it (backward compatibility)
        if (!string.IsNullOrEmpty(options.Issuer))
        {
            logger.LogInformation("✅ [GetIssuer] Using configured Issuer: {Issuer}", options.Issuer);
            return options.Issuer;
        }

        // Use PublicBaseUrl if configured (for Docker/proxy scenarios), otherwise use request URL
        var baseUrl = !string.IsNullOrEmpty(options.PublicBaseUrl)
            ? options.PublicBaseUrl
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        
        logger.LogInformation("🔍 [GetIssuer] PublicBaseUrl={PublicBaseUrl}, BaseUrl={BaseUrl}", 
            options.PublicBaseUrl ?? "(null)", baseUrl);

        // Use mode-aware issuer builder to construct tenant-specific issuer
        var issuerBuilder = httpContext.RequestServices.GetRequiredService<IIssuerBuilder>();
        var issuer = issuerBuilder.BuildIssuer(baseUrl);
        
        logger.LogInformation("✅ [GetIssuer] Final issuer: {Issuer}", issuer);
        
        return issuer;
    }
}
