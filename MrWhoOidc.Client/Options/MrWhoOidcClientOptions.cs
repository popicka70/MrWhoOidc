using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Client.Options;

public sealed class MrWhoOidcClientOptions
{
    private List<string> _scopes = new(["openid"]);

    [Required]
    public string? Issuer { get; set; }

    public Uri? DiscoveryUri { get; set; }

    [Required]
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret for confidential clients. Mutually exclusive with <see cref="ClientAssertion"/>.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// A pre-built client assertion (e.g., JWT). If provided, <see cref="ClientAssertionType"/> must also be set.
    /// </summary>
    public string? ClientAssertion { get; set; }

    public string ClientAssertionType { get; set; } = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public bool PublicClient { get; set; }

    /// <summary>
    /// Additional scopes to request. Defaults to <c>openid</c>.
    /// </summary>
    public IReadOnlyList<string> Scopes
    {
        get => _scopes;
        set => _scopes = value?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? new(["openid"]);
    }

    /// <summary>
    /// Optional OAuth 2.0 resource indicator (RFC 8707).
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// Optional audience value for token exchange.
    /// </summary>
    public string? Audience { get; set; }

    public bool UsePkce { get; set; } = true;

    public bool UseDpop { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public TimeSpan MetadataRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BackchannelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string HttpClientName { get; set; } = MrWhoOidcClientDefaults.DefaultHttpClientName;

    /// <summary>
    /// Optional override for the token endpoint once discovery is cached.
    /// </summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>
    /// Optional override for the authorization endpoint once discovery is cached.
    /// </summary>
    public Uri? AuthorizationEndpoint { get; set; }

    /// <summary>
    /// Optional name for correlating multiple client registrations.
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// On-behalf-of token exchange registrations keyed by logical downstream API name.
    /// </summary>
    public IDictionary<string, OnBehalfOfRegistration> OnBehalfOf { get; } = new Dictionary<string, OnBehalfOfRegistration>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Machine-to-machine client credentials registrations keyed by logical service name.
    /// </summary>
    public IDictionary<string, ClientCredentialsRegistration> ClientCredentials { get; } = new Dictionary<string, ClientCredentialsRegistration>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, accepts any server certificate (including self-signed).
    /// WARNING: Only use for development/demo environments!
    /// </summary>
    public bool DangerousAcceptAnyServerCertificateValidator { get; set; }

    public JarOptions Jar { get; } = new();

    public JarmOptions Jarm { get; } = new();

    public LogoutClientOptions Logout { get; } = new();
}
