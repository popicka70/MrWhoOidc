namespace MrWhoOidc.Auth.Services;

public sealed class AuthOptions
{
    public string[] ApiAudiences { get; set; } = ["api"]; // default

    // Opaque access token issuance options (global or per-audience)
    public OpaqueAccessTokenOptions OpaqueAccessTokens { get; set; } = new();

    // Introspection policy: which clients can introspect which audiences
    // Key: client_id, Value: allowed audience(s) for introspection
    public Dictionary<string, string[]> IntrospectionPermissions { get; set; } = new();

   // Whether refresh token introspection is allowed. If false, RT introspection always returns inactive.
   public bool AllowRefreshTokenIntrospection { get; set; } = false;
}

public sealed class OpaqueAccessTokenOptions
{
    // If true, issue opaque access tokens instead of JWTs.
    public bool Enabled { get; set; }

    // Optional audience allow-list for which opaque tokens are issued. If null/empty and Enabled=true, applies globally.
    public string[]? Audiences { get; set; }
}
