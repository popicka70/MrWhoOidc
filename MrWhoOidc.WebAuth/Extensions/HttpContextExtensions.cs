using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for HttpContext.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Gets the issuer URL for the current request, using the configured issuer or falling back to the request's scheme and host.
    /// </summary>
    /// <param name="httpContext">The HTTP context.</param>
    /// <param name="options">The OIDC options containing the configured issuer.</param>
    /// <returns>The issuer URL.</returns>
    public static string GetIssuer(this HttpContext httpContext, OidcOptions options)
    {
        return options.Issuer ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    }
}
