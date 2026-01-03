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

    // mTLS client authentication for introspection: map client_id -> allowed certificate thumbprints.
    // Thumbprints may be provided as RFC 8705 x5t#S256 (base64url) or SHA-256 hex fingerprint and are case-insensitive.
    public Dictionary<string, string[]> IntrospectionMtlsCertificates { get; set; } = new();

    // mTLS client authentication for revocation: map client_id -> allowed certificate thumbprints
    // Thumbprints may be provided as RFC 8705 x5t#S256 (base64url) or SHA-256 hex fingerprint and are case-insensitive.
    public Dictionary<string, string[]> RevocationMtlsCertificates { get; set; } = new();

    // Optional RFC 8705 mtls_endpoint_aliases base URL.
    // When set to an absolute URL (e.g., https://mtls.example.com), discovery will include mtls_endpoint_aliases
    // pointing at token/introspection/revocation endpoints under that base. The operator is responsible for ensuring
    // those aliases are actually protected by client certificate requirements at the edge/proxy.
    public string? MtlsEndpointAliasesBaseUrl { get; set; }

    // === JAR/PAR policy ===
    // Require PAR globally (request_uri must be used; direct 'request' not accepted). Useful for large request objects or privacy.
    public bool RequirePar { get; set; } = false;
    // Per-client override to require PAR (in addition to global flag). If empty, only global flag applies.
    public string[] RequireParClients { get; set; } = Array.Empty<string>();
    // Maximum size in bytes for a request object (JWT) accepted via /authorize?request= or /par form 'request'. 0 or negative disables the limit.
    public int RequestObjectMaxBytes { get; set; } = 4096;
    // Max lifetime (exp - (iat or nbf)) allowed for a request object. 0 disables the limit.
    public int RequestObjectMaxLifetimeSeconds { get; set; } = 300; // 5 minutes
    // Clock skew applied when validating request object lifetime (seconds). If <=0, defaults to 120s.
    public int RequestObjectClockSkewSeconds { get; set; } = 120;

    // Allowed JAR signing algorithms (global allow-list). Examples: RS256, PS256, ES256, ES384, ES512
    public string[] RequestObjectAllowedAlgorithms { get; set; } = ["RS256", "PS256", "ES256", "ES384", "ES512"];
    // Optional per-client allow-list for JAR signing algorithms. Key: client_id
    public Dictionary<string, string[]> RequestObjectAllowedAlgorithmsPerClient { get; set; } = new();
    // Replay protection TTL (seconds) used when 'exp' is missing; otherwise, expiry uses 'exp' (+skew)
    public int RequestObjectReplayTtlSeconds { get; set; } = 300;

    // PAR per-client pending entries cap (in-memory enforcement)
    public int ParClientPendingLimit { get; set; } = 50;

    // === Claim propagation policy ===
    // Emit amr consistently in ID and access tokens when available from upstream.
    public bool EmitAmrInIdToken { get; set; } = true;
    public bool EmitAmrInAccessToken { get; set; } = true;

    // When enabled and an OIDC 'claims' request includes an 'id_token' member with one or more claims,
    // the ID token will only include explicitly requested payload claims (plus required 'sub').
    // This does not affect structural JWT claims (iss/aud/exp/iat) or JWT-layer OIDC fields (nonce/auth_time/at_hash).
    public bool RestrictIdTokenClaimsToClaimsRequest { get; set; } = false;
    // Allow-list of mapped claim names we may propagate into ID/access tokens when present and policy allows.
    public string[] PropagateMappedClaimsToIdToken { get; set; } = Array.Empty<string>();
    public string[] PropagateMappedClaimsToAccessToken { get; set; } = Array.Empty<string>();

    // Default claim mappings applied when a provider has no explicit mappings in the database.
    public ClaimMappingRule[] DefaultClaimMappings { get; set; } = Array.Empty<ClaimMappingRule>();

    // === Features ===
    // Enable OAuth 2.0 Token Exchange (RFC 8693) at the token endpoint.
    // When disabled, the grant is not accepted and not advertised in discovery.
    public bool EnableTokenExchange { get; set; } = false;

    // === Optional public JWKS exposure ===
    // Expose per-client JWKS at /clients/{client_id}/jwks (serves PublicJwksJson if present)
    public bool ExposeClientJwks { get; set; } = false;
    // Expose per-provider JWKS at /providers/{provider_name}/jwks (active signing keys only)
    public bool ExposeProviderJwks { get; set; } = false;
    // Expose aggregated provider JWKS at /providers/jwks (all enabled providers' active signing keys)
    public bool ExposeAggregatedProviderJwks { get; set; } = false;
    // Cache lifetimes (seconds) for client/provider JWKS HTTP responses + in-memory cache entries
    public int ClientJwksCacheSeconds { get; set; } = 300;
    public int ProviderJwksCacheSeconds { get; set; } = 300;
    // Include encryption purpose keys when exposing provider JWKS (off by default)
    public bool ProviderJwksIncludeEncryption { get; set; } = false;

    // === OIDC Discovery metadata (optional) ===
    // Documentation/policy URLs are advertised only when set.
    public string? ServiceDocumentationUrl { get; set; }
    public string? OpPolicyUrl { get; set; }
    public string? OpTosUrl { get; set; }
    // UI locale hints (BCP47 tags). Empty => omitted from discovery.
    public string[] UiLocalesSupported { get; set; } = Array.Empty<string>();
    // Optional list of supported ACR values advertised in discovery.
    // If empty, acr_values_supported is omitted and acr_values requests are not validated against a fixed allow-list.
    public string[] AcrValuesSupported { get; set; } = Array.Empty<string>();
}

public sealed class OpaqueAccessTokenOptions
{
    // Whether to issue opaque tokens for all API audiences (JWT otherwise). Per-audience overrides may apply.
    public bool Enabled { get; set; } = false;
    // Legacy audience allow-list used by TokenService; if empty, all audiences are eligible when Enabled=true.
    public string[]? Audiences { get; set; } = Array.Empty<string>();
    // Per-audience enablement (overrides global). Key = audience string.
    public Dictionary<string, bool> PerAudience { get; set; } = new();
}

public sealed class ClaimMappingRule
{
    public string ExternalClaim { get; set; } = string.Empty;
    public string LocalClaim { get; set; } = string.Empty;
    public string? Transform { get; set; }
    public int Order { get; set; } = 0;
}
