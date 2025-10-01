namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Extension methods for logout-related operations.
/// </summary>
public static class LogoutExtensions
{
    /// <summary>
    /// Gets the issuer URL from OidcOptions or constructs from request.
    /// </summary>
    public static string GetIssuer(this HttpContext http)
    {
        var options = http.RequestServices.GetService(typeof(OidcOptions)) as OidcOptions;
        return options?.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
    }
}
