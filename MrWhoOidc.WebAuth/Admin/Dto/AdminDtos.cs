namespace MrWhoOidc.WebAuth.Admin.Dto;

public sealed record MappingInput(Guid IdentityProviderId, bool Enabled, bool IsDefaultForClient, bool AutoRedirectIfSingle, string? RequiredAcr, int Order);
public sealed record ClaimMappingInput(string ExternalClaim, string LocalClaim, string? Transform, int Order);
public sealed record ProviderKeyInput(MrWhoOidc.Auth.Persistence.IdentityProviderKeyPurpose Purpose, string Alg, string? Kid, bool Active, string JwkJson, DateTimeOffset? ExpiresAt);
public sealed record ClientKeysInput(string? PublicJwksJson, string? PublicJwksUri);

// Realm management
public sealed record RealmInput(string? Name, string? DisplayName, bool? AllowUnconfirmedLogin);

// Client management
public sealed record CreateClientInput(
    string? ClientId,
    string? ClientName,
    Guid RealmId,
    bool? RequirePkce,
    bool? RequireConsent,
    string? Scope,
    List<string>? GrantTypes,
    List<string>? AllowedLoginRedirectUris,
    List<string>? AllowedLogoutRedirectUris,
    bool? CreateInitialSecret);

// Scope management
public sealed record ScopeInput(string? Name, string? Description, bool? IsExposed);

// User management
public sealed record CreateUserInput(string? Username, string? Email, string? Name, string? Password);
public sealed record UpdateUserInput(string? Name, string? Email);

// Client update (partial — only non-null fields are applied)
public sealed record UpdateClientInput(
    string? ClientName,
    bool? RequirePkce,
    bool? RequireConsent,
    bool? RequirePar,
    string? Scope,
    List<string>? GrantTypes,
    List<string>? AllowedLoginRedirectUris,
    List<string>? AllowedLogoutRedirectUris,
    string? BackChannelLogoutUri,
    string? FrontChannelLogoutUri,
    string? TokenEndpointAuthMethod,
    bool? OboEnabled,
    bool? AllowLocalLogin,
    bool? AllowExternalIdp);

// Role management
public sealed record RoleInput(string? Name, Guid? RealmId);

// User ↔ Role assignment
public sealed record UserRoleAssignInput(Guid RoleId);

// User ↔ Client assignment
public sealed record UserClientAssignInput(Guid ClientId);

// Client ↔ Scope management
public sealed record ClientScopeAssignInput(string ScopeName);

// Tenant update (platform-admin)
public sealed record UpdateTenantInput(string? Name, string? Description, string? AdminEmail, string? Status, int? MaxUsers, int? MaxClients);
