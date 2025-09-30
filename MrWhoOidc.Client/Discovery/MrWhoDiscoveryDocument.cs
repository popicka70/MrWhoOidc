using System.Text.Json.Serialization;

namespace MrWhoOidc.Client.Discovery;

public sealed class MrWhoDiscoveryDocument
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; init; }

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserInfoEndpoint { get; init; }

    [JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; init; }

    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; init; }

    [JsonPropertyName("scopes_supported")]
    public string[]? ScopesSupported { get; init; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public string[]? TokenEndpointAuthMethods { get; init; }

    public Uri RequireHttps(string? value, bool requireHttps = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Discovery document missing required value.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Value '{value}' is not an absolute URI.");
        }

        if (requireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Value '{value}' must be HTTPS when RequireHttpsMetadata is enabled.");
        }

        return uri;
    }
}
