namespace MrWhoOidc.Auth.Services;

public sealed class AuthOptions
{
    public string[] ApiAudiences { get; set; } = ["api"]; // default

    // Opaque access token issuance options (global or per-audience)
    public OpaqueAccessTokenOptions OpaqueAccessTokens { get; set; } = new();

    // Introspection policy: which clients can introspect which audiences
    // Key: client_id, Value: allowed audience(s) for introspection
    public Dictionary<string, string[]> IntrospectionPermissions { get; set; } = new();

    // Introspection response shaping (privacy by default):
    // - Default set of fields included in introspection responses when no per-client override is specified.
    // - Per-client allow-list of response fields to include. Keys not in the allow-list are removed.
    //   Example fields: active, token_type, scope, sub, username, aud, iss, iat, nbf, exp, jti, cnf, client_id
    public string[] IntrospectionDefaultResponseFields { get; set; } = ["active", "token_type", "scope", "sub", "aud", "iss", "exp"]; // privacy-friendly baseline
    public Dictionary<string, string[]> IntrospectionResponseFields { get; set; } = new();

    // Whether refresh token introspection is allowed. If false, RT introspection always returns inactive.
    public bool AllowRefreshTokenIntrospection { get; set; } = false;

    // mTLS client authentication for introspection: map client_id -> allowed certificate thumbprints
    // Thumbprints should be provided as hex without spaces and are case-insensitive.
    public Dictionary<string, string[]> IntrospectionMtlsCertificates { get; set; } = new();
}

public sealed class OpaqueAccessTokenOptions
{
    // If true, issue opaque access tokens instead of JWTs.
    public bool Enabled { get; set; }

    // Optional audience allow-list for which opaque tokens are issued. If null/empty and Enabled=true, applies globally.
    public string[]? Audiences { get; set; }
}
