using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Defines the context in which client authentication is being performed.
/// </summary>
public enum ClientAuthenticationUsage
{
    TokenEndpoint,
    Introspection,
    Revocation,
    Other
}

/// <summary>
/// Represents the input for client authentication, decoupled from HTTP.
/// </summary>
public record ClientCredentialInput(
    string ClientId,
    ClientAuthenticationUsage Usage = ClientAuthenticationUsage.Other,
    string? GrantType = null,
    string? ClientSecret = null,
    string? ClientAssertionType = null,
    string? ClientAssertion = null,
    // RFC 8705 self_signed_tls_client_auth: x5t#S256 (base64url) of DER-encoded certificate
    string? MtlsThumbprint = null,
    // Back-compat: SHA-256 certificate fingerprint as hex (GetCertHashString(HashAlgorithmName.SHA256))
    string? MtlsThumbprintHexSha256 = null,
    string? EndpointUrl = null,
    // Issuer identifier; also accepted as private_key_jwt assertion audience.
    string? Issuer = null,
    // Further endpoint-specific private_key_jwt audiences (e.g. the PAR endpoint URL built from the issuer).
    IReadOnlyList<string>? AdditionalAudiences = null);

/// <summary>
/// Represents the result of a client authentication attempt.
/// </summary>
public record ClientAuthResult(
    bool IsSuccess,
    Client? Client = null,
    string? Error = null,
    string? ErrorDescription = null,
    // True when the client was authenticated by an endpoint mTLS allow-list (introspection/revocation).
    bool AuthenticatedByMtlsAllowList = false);

