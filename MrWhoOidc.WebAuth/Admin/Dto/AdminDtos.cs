namespace MrWhoOidc.WebAuth.Admin.Dto;

public sealed record MappingInput(Guid IdentityProviderId, bool Enabled, bool IsDefaultForClient, bool AutoRedirectIfSingle, string? RequiredAcr, int Order);
public sealed record ClaimMappingInput(string ExternalClaim, string LocalClaim, string? Transform, int Order);
public sealed record ProviderKeyInput(MrWhoOidc.Auth.Persistence.IdentityProviderKeyPurpose Purpose, string Alg, string? Kid, bool Active, string JwkJson, DateTimeOffset? ExpiresAt);
public sealed record ClientKeysInput(string? PublicJwksJson, string? PublicJwksUri);
