namespace MrWhoOidc.Auth.Options;

public sealed class OidcOptions
{
    public string? Issuer { get; set; }
    
    /// <summary>
    /// Public-facing base URL (scheme + host) used when running behind a proxy or in Docker.
    /// Example: "https://localhost:8443" or "https://auth.example.com"
    /// If not set, the issuer will be built from the HTTP request's scheme and host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
    
    public string[] AllowedPostLogoutRedirectUris { get; set; } = [];
    public string[] AllowedCorsOrigins { get; set; } = [];

    /// <summary>
    /// Base URL for the sample web client used during tenant seeding.
    /// Default: "https://localhost:5001"
    /// </summary>
    public string SampleWebClientBaseUrl { get; set; } = "https://localhost:5001";
}
