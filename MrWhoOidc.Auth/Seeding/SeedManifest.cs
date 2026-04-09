using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Seed manifest intended to be portable (future import/export).
/// Prefer referencing tenants and realms by name/slug rather than by IDs.
/// </summary>
public sealed record SeedManifest
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Optional platform-wide settings to apply during dev/test seeding.
    /// </summary>
    [JsonPropertyName("platformSettings")]
    public PlatformSettingsSeedDefinition? PlatformSettings { get; init; }

    /// <summary>
    /// Optional platform-managed initial access tokens for RFC 7591 dynamic client registration.
    /// These are seeded as plaintext values and stored as hashes.
    /// </summary>
    [JsonPropertyName("platformInitialAccessTokens")]
    public List<PlatformInitialAccessTokenSeedDefinition> PlatformInitialAccessTokens { get; init; } = [];

    /// <summary>
    /// Optional license definitions to install at startup (dev/test seeding).
    /// </summary>
    [JsonPropertyName("licenses")]
    public List<LicenseSeedDefinition> Licenses { get; init; } = [];

    /// <summary>
    /// Optional explicit scope registry definitions.
    /// These are written to the Scopes table so they can be referenced by ClientScopes.
    /// </summary>
    [JsonPropertyName("scopes")]
    public List<ScopeSeedDefinition> Scopes { get; init; } = [];

    [JsonPropertyName("tenants")]
    public List<TenantSeedDefinition> Tenants { get; init; } = [];

    /// <summary>
    /// Standalone realm definitions for realm-level export/import.
    /// </summary>
    [JsonPropertyName("realms")]
    public List<RealmSeedDefinition> Realms { get; init; } = [];

    /// <summary>
    /// Standalone client definitions for client-level export/import.
    /// </summary>
    [JsonPropertyName("clients")]
    public List<ClientSeedDefinition> Clients { get; init; } = [];

    /// <summary>
    /// Standalone identity provider definitions for provider-level export/import.
    /// </summary>
    [JsonPropertyName("identityProviders")]
    public List<IdentityProviderSeedDefinition> IdentityProviders { get; init; } = [];
}

public sealed record LicenseSeedDefinition
{
    /// <summary>
    /// Inline license token (JWT). Prefer using licenseTokenPath or licenseTokenEnv to avoid committing secrets.
    /// </summary>
    [JsonPropertyName("licenseToken")]
    public string? LicenseToken { get; init; }

    /// <summary>
    /// Optional path to a file containing the license token.
    /// Useful for Docker volume mounts.
    /// </summary>
    [JsonPropertyName("licenseTokenPath")]
    public string? LicenseTokenPath { get; init; }

    /// <summary>
    /// Optional environment/config key that contains the license token.
    /// </summary>
    [JsonPropertyName("licenseTokenEnv")]
    public string? LicenseTokenEnv { get; init; }

    /// <summary>
    /// Optional notes recorded during installation.
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>
    /// License scope: "platform" (default) or "tenant".
    /// For tenant scope, tenantSlug is required.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>
    /// Tenant slug for tenant-scoped licenses.
    /// </summary>
    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; init; }
}

public sealed record ScopeSeedDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// If true, scope is global (TenantId is null). If false, scope is tenant-scoped.
    /// Defaults to true when tenantSlug is not provided.
    /// </summary>
    [JsonPropertyName("isGlobal")]
    public bool? IsGlobal { get; init; }

    [JsonPropertyName("isExposed")]
    public bool? IsExposed { get; init; }

    /// <summary>
    /// Optional tenant slug for tenant-scoped scopes.
    /// When set, the scope will be associated with that tenant.
    /// </summary>
    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; init; }
}

public sealed record PlatformSettingsSeedDefinition
{
    [JsonPropertyName("qrLoginAtDiscoveryEnabled")]
    public bool? QrLoginAtDiscoveryEnabled { get; init; }

    [JsonPropertyName("dynamicClientRegistrationEnabled")]
    public bool? DynamicClientRegistrationEnabled { get; init; }

    [JsonPropertyName("enableTokenExchange")]
    public bool? EnableTokenExchange { get; init; }
}

public sealed record PlatformInitialAccessTokenSeedDefinition
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }
}

public sealed record TenantSeedDefinition
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Optional. If omitted, the issuer will be computed from configured base URL + tenant slug.
    /// </summary>
    [JsonPropertyName("issuerUri")]
    public string? IssuerUri { get; init; }

    [JsonPropertyName("adminEmail")]
    public string? AdminEmail { get; init; }

    [JsonPropertyName("billingPlan")]
    public string? BillingPlan { get; init; }

    /// <summary>
    /// Optional realm name that dynamically registered clients should be assigned to.
    /// Null leaves the existing tenant setting unchanged.
    /// Empty string disables dynamic client registration for this tenant.
    /// </summary>
    [JsonPropertyName("dynamicClientRegistrationRealm")]
    public string? DynamicClientRegistrationRealm { get; init; }

    // --- Export/Import extensions ---

    /// <summary>
    /// Tenant status: "Active", "Suspended", or "Disabled".
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>
    /// URL to tenant logo image.
    /// </summary>
    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; init; }

    /// <summary>
    /// Primary branding color (hex format).
    /// </summary>
    [JsonPropertyName("primaryColor")]
    public string? PrimaryColor { get; init; }

    /// <summary>
    /// Accent branding color (hex format).
    /// </summary>
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; init; }

    /// <summary>
    /// Custom tenant settings as JSON blob.
    /// </summary>
    [JsonPropertyName("settingsJson")]
    public string? SettingsJson { get; init; }

    /// <summary>
    /// License limit: maximum number of users.
    /// </summary>
    [JsonPropertyName("maxUsers")]
    public int? MaxUsers { get; init; }

    /// <summary>
    /// License limit: maximum number of clients.
    /// </summary>
    [JsonPropertyName("maxClients")]
    public int? MaxClients { get; init; }

    [JsonPropertyName("realms")]
    public List<RealmSeedDefinition> Realms { get; init; } = [];

    [JsonPropertyName("clients")]
    public List<ClientSeedDefinition> Clients { get; init; } = [];

    /// <summary>
    /// Identity providers configured for this tenant.
    /// </summary>
    [JsonPropertyName("identityProviders")]
    public List<IdentityProviderSeedDefinition> IdentityProviders { get; init; } = [];

    /// <summary>
    /// Roles defined within this tenant's realms.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<RoleSeedDefinition> Roles { get; init; } = [];

    /// <summary>
    /// Users associated with this tenant.
    /// </summary>
    [JsonPropertyName("users")]
    public List<UserSeedDefinition> Users { get; init; } = [];
}

public sealed record RealmSeedDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("allowUnconfirmedLogin")]
    public bool? AllowUnconfirmedLogin { get; init; }

    /// <summary>
    /// Clients within this realm (for realm-level export).
    /// </summary>
    [JsonPropertyName("clients")]
    public List<ClientSeedDefinition> Clients { get; init; } = [];

    /// <summary>
    /// Roles within this realm (for realm-level export).
    /// </summary>
    [JsonPropertyName("roles")]
    public List<RoleSeedDefinition> Roles { get; init; } = [];
}

public sealed record ClientSeedDefinition
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("clientName")]
    public required string ClientName { get; init; }

    /// <summary>
    /// Realm name within the tenant. Defaults to "admin" if omitted.
    /// </summary>
    [JsonPropertyName("realm")]
    public string? Realm { get; init; }

    [JsonPropertyName("requirePkce")]
    public bool? RequirePkce { get; init; }

    [JsonPropertyName("requireConsent")]
    public bool? RequireConsent { get; init; }

    /// <summary>
    /// Whether Pushed Authorization Request (PAR) is required.
    /// </summary>
    [JsonPropertyName("requirePar")]
    public bool? RequirePar { get; init; }

    /// <summary>
    /// One of: No, All, OnlyExternalIdp.
    /// Stored in Clients.AutoApprovalMode.
    /// </summary>
    [JsonPropertyName("autoApprovalMode")]
    public string? AutoApprovalMode { get; init; }

    /// <summary>
    /// Optional initial secret (dev/test). Stored using the existing hashing scheme.
    /// For production import/export, prefer managing secrets via dedicated APIs.
    /// </summary>
    [JsonPropertyName("clientSecret")]
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Optional: name of an environment/config key whose value contains the client secret.
    /// This keeps secrets out of the manifest while allowing env-file driven seeding.
    /// </summary>
    [JsonPropertyName("clientSecretEnv")]
    public string? ClientSecretEnv { get; init; }

    /// <summary>
    /// Hashed client secret value (for full exports only).
    /// Never contains plaintext secrets.
    /// </summary>
    [JsonPropertyName("clientSecretHash")]
    public string? ClientSecretHash { get; init; }

    // --- Public Keys ---

    /// <summary>
    /// Inline JWKS for client authentication (e.g., private_key_jwt).
    /// </summary>
    [JsonPropertyName("publicJwksJson")]
    public string? PublicJwksJson { get; init; }

    /// <summary>
    /// Remote JWKS URI for dynamic key retrieval.
    /// </summary>
    [JsonPropertyName("publicJwksUri")]
    public string? PublicJwksUri { get; init; }

    [JsonPropertyName("allowedLoginRedirectUris")]
    public List<string> AllowedLoginRedirectUris { get; init; } = [];

    [JsonPropertyName("allowedLogoutRedirectUris")]
    public List<string> AllowedLogoutRedirectUris { get; init; } = [];

    // --- Subject Identifiers ---

    /// <summary>
    /// OpenID Connect subject type: "public" or "pairwise".
    /// </summary>
    [JsonPropertyName("subjectType")]
    public string? SubjectType { get; init; }

    /// <summary>
    /// Optional sector_identifier_uri for pairwise subject identifiers.
    /// </summary>
    [JsonPropertyName("sectorIdentifierUri")]
    public string? SectorIdentifierUri { get; init; }

    // --- OIDC Response Crypto (optional) ---

    /// <summary>
    /// JWS alg for signed ID tokens.
    /// Note: this server enforces tenant active signing alg.
    /// </summary>
    [JsonPropertyName("idTokenSignedResponseAlg")]
    public string? IdTokenSignedResponseAlg { get; init; }

    /// <summary>
    /// JWE alg for encrypted ID tokens (e.g., "RSA-OAEP").
    /// </summary>
    [JsonPropertyName("idTokenEncryptedResponseAlg")]
    public string? IdTokenEncryptedResponseAlg { get; init; }

    /// <summary>
    /// JWE enc for encrypted ID tokens (e.g., "A256CBC-HS512").
    /// </summary>
    [JsonPropertyName("idTokenEncryptedResponseEnc")]
    public string? IdTokenEncryptedResponseEnc { get; init; }

    /// <summary>
    /// JWS alg for signed UserInfo responses (application/jwt).
    /// Note: this server enforces tenant active signing alg.
    /// </summary>
    [JsonPropertyName("userInfoSignedResponseAlg")]
    public string? UserInfoSignedResponseAlg { get; init; }

    /// <summary>
    /// JWE alg for encrypted UserInfo responses.
    /// </summary>
    [JsonPropertyName("userInfoEncryptedResponseAlg")]
    public string? UserInfoEncryptedResponseAlg { get; init; }

    /// <summary>
    /// JWE enc for encrypted UserInfo responses.
    /// </summary>
    [JsonPropertyName("userInfoEncryptedResponseEnc")]
    public string? UserInfoEncryptedResponseEnc { get; init; }

    /// <summary>
    /// JWS alg for JARM authorization responses (query.jwt / fragment.jwt / form_post.jwt).
    /// Note: this server enforces tenant active signing alg.
    /// </summary>
    [JsonPropertyName("authorizationSignedResponseAlg")]
    public string? AuthorizationSignedResponseAlg { get; init; }

    /// <summary>
    /// JWE alg for encrypted JARM authorization responses.
    /// </summary>
    [JsonPropertyName("authorizationEncryptedResponseAlg")]
    public string? AuthorizationEncryptedResponseAlg { get; init; }

    /// <summary>
    /// JWE enc for encrypted JARM authorization responses.
    /// </summary>
    [JsonPropertyName("authorizationEncryptedResponseEnc")]
    public string? AuthorizationEncryptedResponseEnc { get; init; }

    // --- Login Methods ---

    /// <summary>
    /// Whether local (username/password) login is allowed.
    /// </summary>
    [JsonPropertyName("allowLocalLogin")]
    public bool? AllowLocalLogin { get; init; }

    /// <summary>
    /// Whether external IdP login is allowed.
    /// </summary>
    [JsonPropertyName("allowExternalIdp")]
    public bool? AllowExternalIdp { get; init; }

    /// <summary>
    /// Whether QR code login is allowed.
    /// </summary>
    [JsonPropertyName("allowQrLogin")]
    public bool? AllowQrLogin { get; init; }

    // --- Logout URIs ---

    /// <summary>
    /// Back-channel logout URI for receiving logout tokens.
    /// </summary>
    [JsonPropertyName("backChannelLogoutUri")]
    public string? BackChannelLogoutUri { get; init; }

    /// <summary>
    /// Whether session ID (sid) is required in back-channel logout tokens.
    /// </summary>
    [JsonPropertyName("backChannelLogoutSessionRequired")]
    public bool? BackChannelLogoutSessionRequired { get; init; }

    /// <summary>
    /// Front-channel logout URI.
    /// </summary>
    [JsonPropertyName("frontChannelLogoutUri")]
    public string? FrontChannelLogoutUri { get; init; }

    /// <summary>
    /// Whether session ID (sid) is required in front-channel logout.
    /// </summary>
    [JsonPropertyName("frontChannelLogoutSessionRequired")]
    public bool? FrontChannelLogoutSessionRequired { get; init; }

    /// <summary>
    /// Optional explicit allow-list of OIDC scopes for this client.
    /// When provided, this maps to ClientScopes rows and enables fail-closed scope validation.
    /// </summary>
    [JsonPropertyName("allowedScopes")]
    public List<string> AllowedScopes { get; init; } = [];

    // OBO / Token Exchange policy (optional)
    // These map to Client.Obo* fields and are only applied when present.
    [JsonPropertyName("oboEnabled")]
    public bool? OboEnabled { get; init; }

    [JsonPropertyName("oboAllowedSourceAudiences")]
    public List<string> OboAllowedSourceAudiences { get; init; } = [];

    [JsonPropertyName("oboAllowedTargetAudiences")]
    public List<string> OboAllowedTargetAudiences { get; init; } = [];

    [JsonPropertyName("oboAllowedScopes")]
    public List<string> OboAllowedScopes { get; init; } = [];

    /// <summary>
    /// Maximum delegation depth for OBO chains.
    /// </summary>
    [JsonPropertyName("oboMaxDelegationDepth")]
    public int? OboMaxDelegationDepth { get; init; }

    /// <summary>
    /// Maximum token lifetime in minutes for OBO tokens.
    /// </summary>
    [JsonPropertyName("oboMaxLifetimeMinutes")]
    public int? OboMaxLifetimeMinutes { get; init; }

    /// <summary>
    /// DPoP mode for OBO: "None", "Optional", or "Required".
    /// </summary>
    [JsonPropertyName("oboDpopMode")]
    public string? OboDpopMode { get; init; }

    /// <summary>
    /// List of client IDs that are allowed to call this client via OBO.
    /// </summary>
    [JsonPropertyName("oboAllowedCallers")]
    public List<string> OboAllowedCallers { get; init; } = [];

    // --- M2M Settings ---

    /// <summary>
    /// Allowed audiences for machine-to-machine tokens.
    /// </summary>
    [JsonPropertyName("m2mAllowedAudiences")]
    public List<string> M2mAllowedAudiences { get; init; } = [];

    /// <summary>
    /// Access token lifetime in seconds for M2M tokens.
    /// </summary>
    [JsonPropertyName("m2mAccessTokenLifetimeSeconds")]
    public int? M2mAccessTokenLifetimeSeconds { get; init; }

    // --- Auto-assignment ---

    /// <summary>
    /// Whether to auto-assign new users to this client.
    /// </summary>
    [JsonPropertyName("autoAssignNewUsersToClient")]
    public bool? AutoAssignNewUsersToClient { get; init; }

    // --- IdP Assignments ---

    /// <summary>
    /// Identity provider assignments for this client.
    /// </summary>
    [JsonPropertyName("identityProviderAssignments")]
    public List<ClientIdpAssignmentSeedDefinition> IdentityProviderAssignments { get; init; } = [];
}
