using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;

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
        var options = http.RequestServices.GetRequiredService<OidcOptions>();
        return MrWhoOidc.WebAuth.Extensions.HttpContextExtensions.GetIssuer(http, options);
    }
}
