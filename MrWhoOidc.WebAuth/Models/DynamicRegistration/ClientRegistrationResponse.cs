using System.Text.Json.Serialization;

namespace MrWhoOidc.WebAuth.Models.DynamicRegistration;

/// <summary>
/// RFC 7591 - OAuth 2.0 Dynamic Client Registration Protocol
/// Client registration response returned to client after successful registration.
/// </summary>
public sealed class ClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("client_id_issued_at")]
    public long ClientIdIssuedAt { get; set; }

    [JsonPropertyName("client_secret_expires_at")]
    public long ClientSecretExpiresAt { get; set; }

    [JsonPropertyName("registration_access_token")]
    public string? RegistrationAccessToken { get; set; }

    [JsonPropertyName("registration_client_uri")]
    public string? RegistrationClientUri { get; set; }

    // Echo back all client metadata from request
    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; set; } = new();

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }

    [JsonPropertyName("grant_types")]
    public List<string>? GrantTypes { get; set; }

    [JsonPropertyName("response_types")]
    public List<string>? ResponseTypes { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("client_uri")]
    public string? ClientUri { get; set; }

    [JsonPropertyName("logo_uri")]
    public string? LogoUri { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("contacts")]
    public List<string>? Contacts { get; set; }

    [JsonPropertyName("tos_uri")]
    public string? TosUri { get; set; }

    [JsonPropertyName("policy_uri")]
    public string? PolicyUri { get; set; }

    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; set; }

    [JsonPropertyName("jwks")]
    public object? Jwks { get; set; }

    [JsonPropertyName("software_id")]
    public string? SoftwareId { get; set; }

    [JsonPropertyName("software_version")]
    public string? SoftwareVersion { get; set; }

    [JsonPropertyName("application_type")]
    public string? ApplicationType { get; set; }

    [JsonPropertyName("sector_identifier_uri")]
    public string? SectorIdentifierUri { get; set; }

    [JsonPropertyName("subject_type")]
    public string? SubjectType { get; set; }

    [JsonPropertyName("id_token_signed_response_alg")]
    public string? IdTokenSignedResponseAlg { get; set; }

    [JsonPropertyName("id_token_encrypted_response_alg")]
    public string? IdTokenEncryptedResponseAlg { get; set; }

    [JsonPropertyName("id_token_encrypted_response_enc")]
    public string? IdTokenEncryptedResponseEnc { get; set; }

    [JsonPropertyName("userinfo_signed_response_alg")]
    public string? UserinfoSignedResponseAlg { get; set; }

    [JsonPropertyName("userinfo_encrypted_response_alg")]
    public string? UserinfoEncryptedResponseAlg { get; set; }

    [JsonPropertyName("userinfo_encrypted_response_enc")]
    public string? UserinfoEncryptedResponseEnc { get; set; }

    [JsonPropertyName("default_max_age")]
    public int? DefaultMaxAge { get; set; }

    [JsonPropertyName("require_auth_time")]
    public bool? RequireAuthTime { get; set; }

    [JsonPropertyName("default_acr_values")]
    public List<string>? DefaultAcrValues { get; set; }

    [JsonPropertyName("backchannel_logout_uri")]
    public string? BackchannelLogoutUri { get; set; }

    [JsonPropertyName("backchannel_logout_session_required")]
    public bool? BackchannelLogoutSessionRequired { get; set; }

    [JsonPropertyName("frontchannel_logout_uri")]
    public string? FrontchannelLogoutUri { get; set; }

    [JsonPropertyName("frontchannel_logout_session_required")]
    public bool? FrontchannelLogoutSessionRequired { get; set; }

    [JsonPropertyName("post_logout_redirect_uris")]
    public List<string>? PostLogoutRedirectUris { get; set; }
}
