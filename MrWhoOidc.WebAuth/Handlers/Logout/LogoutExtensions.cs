using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Extension methods for logout-related operations.
/// </summary>
public static class LogoutExtensions
{
    /// <summary>
    /// Gets the issuer URL from OidcOptions or constructs mode-aware issuer from request.
    /// In single-tenant mode: returns root issuer (e.g., https://auth.example.com)
    /// In multi-tenant mode: returns path-based issuer (e.g., https://auth.example.com/t/tenant-slug)
    /// </summary>
    public static string GetIssuer(this HttpContext http)
    {
        var options = http.RequestServices.GetService(typeof(OidcOptions)) as OidcOptions;
        
        // If issuer is explicitly configured, use it (backward compatibility)
        if (!string.IsNullOrEmpty(options?.Issuer))
        {
            return options.Issuer;
        }

        // Otherwise, use mode-aware issuer builder
        var issuerBuilder = http.RequestServices.GetRequiredService<IIssuerBuilder>();
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return issuerBuilder.BuildIssuer(baseUrl);
    }
}
