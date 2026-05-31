using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Discovery response from an external OIDC provider.
/// </summary>
public sealed class DiscoveryResponse
{
    public required string AuthorizationEndpoint { get; init; }
    public string? PushedAuthorizationRequestEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public string? UserinfoEndpoint { get; init; }
    public required string JwksUri { get; init; }
    public string? Issuer { get; init; }
}

/// <summary>
/// Result of a discovery operation.
/// </summary>
public sealed class DiscoveryResult
{
    public bool Success { get; init; }
    public DiscoveryResponse? Response { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Handles OIDC discovery operations for external providers.
/// </summary>
public interface IExternalOidcDiscoveryService
{
    Task<DiscoveryResult> DiscoverAsync(string authority, string? discoveryUrl, CancellationToken cancellationToken);
}

internal sealed class ExternalOidcDiscoveryService : IExternalOidcDiscoveryService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalOidcDiscoveryService> _logger;

    public ExternalOidcDiscoveryService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<ExternalOidcDiscoveryService> logger)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DiscoveryResult> DiscoverAsync(
        string authority,
        string? discoveryUrl,
        CancellationToken cancellationToken)
    {
        var discoUrl = string.IsNullOrWhiteSpace(discoveryUrl)
            ? authority.TrimEnd('/') + "/.well-known/openid-configuration"
            : discoveryUrl;

        if (!TryResolveExpectedHost(authority, discoUrl, out _, out var discoveryError))
        {
            return new DiscoveryResult
            {
                Success = false,
                ErrorCode = "invalid_discovery_url",
                ErrorMessage = discoveryError
            };
        }

        try
        {
            var (httpc, disposeHttp) = CreateOutboundHttpClient(TimeSpan.FromSeconds(10));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var _ = disposeHttp ? httpc : null;
            using var resp = await httpc.GetAsync(discoUrl, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discovery failed with status {Status} for {Url}", (int)resp.StatusCode, discoUrl);
                return new DiscoveryResult
                {
                    Success = false,
                    ErrorCode = "discovery_failed",
                    ErrorMessage = $"Discovery failed: {(int)resp.StatusCode}"
                };
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;

            var discovery = new DiscoveryResponse
            {
                AuthorizationEndpoint = root.GetProperty("authorization_endpoint").GetString()!,
                PushedAuthorizationRequestEndpoint = root.TryGetProperty("pushed_authorization_request_endpoint", out var parEl)
                    ? parEl.GetString()
                    : null,
                TokenEndpoint = root.GetProperty("token_endpoint").GetString()!,
                UserinfoEndpoint = root.TryGetProperty("userinfo_endpoint", out var uiEl)
                    ? uiEl.GetString()
                    : null,
                JwksUri = root.GetProperty("jwks_uri").GetString()!,
                Issuer = root.TryGetProperty("issuer", out var issEl)
                    ? issEl.GetString()
                    : null
            };

            if (!ValidateEndpoints(discovery, out var endpointError))
            {
                _logger.LogWarning("Discovery endpoint validation failed for {Url}: {Error}", discoUrl, endpointError);
                return new DiscoveryResult
                {
                    Success = false,
                    ErrorCode = "invalid_discovery_endpoint",
                    ErrorMessage = endpointError
                };
            }

            return new DiscoveryResult
            {
                Success = true,
                Response = discovery
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Discovery timeout for {Url}", discoUrl);
            return new DiscoveryResult
            {
                Success = false,
                ErrorCode = "discovery_timeout",
                ErrorMessage = "Discovery request timed out"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery error for {Url}", discoUrl);
            return new DiscoveryResult
            {
                Success = false,
                ErrorCode = "discovery_exception",
                ErrorMessage = $"Discovery error: {ex.Message}"
            };
        }
    }

    private static bool TryResolveExpectedHost(string authority, string discoveryUrl, out string expectedHost, out string error)
    {
        expectedHost = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || !IsHttpUri(authorityUri))
        {
            error = "Authority must be an absolute http or https URI";
            return false;
        }

        if (!Uri.TryCreate(discoveryUrl, UriKind.Absolute, out var discoveryUri) || !IsHttpUri(discoveryUri))
        {
            error = "Discovery URL must be an absolute http or https URI";
            return false;
        }

        expectedHost = authorityUri.IdnHost;
        if (!string.Equals(discoveryUri.IdnHost, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "Discovery URL host must match authority host";
            return false;
        }

        return true;
    }

    // Validates that the discovery document advertises well-formed absolute http(s) endpoints.
    // We intentionally do NOT require endpoint hosts to match the authority host: real-world
    // providers legitimately serve endpoints from different hosts (e.g. Google's authority is
    // accounts.google.com while its token endpoint is oauth2.googleapis.com, and Microsoft's
    // userinfo endpoint is graph.microsoft.com). SSRF to internal addresses is already prevented
    // by the SSRF-safe HTTP client used for all outbound discovery/token/JWKS calls.
    private static bool ValidateEndpoints(DiscoveryResponse discovery, out string error)
    {
        error = string.Empty;

        return ValidateEndpoint(discovery.AuthorizationEndpoint, nameof(discovery.AuthorizationEndpoint), required: true, out error)
            && ValidateEndpoint(discovery.TokenEndpoint, nameof(discovery.TokenEndpoint), required: true, out error)
            && ValidateEndpoint(discovery.JwksUri, nameof(discovery.JwksUri), required: true, out error)
            && ValidateEndpoint(discovery.PushedAuthorizationRequestEndpoint, nameof(discovery.PushedAuthorizationRequestEndpoint), required: false, out error)
            && ValidateEndpoint(discovery.UserinfoEndpoint, nameof(discovery.UserinfoEndpoint), required: false, out error);
    }

    private static bool ValidateEndpoint(string? endpoint, string name, bool required, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (!required) return true;
            error = $"Discovery response is missing {name}";
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !IsHttpUri(uri))
        {
            error = $"Discovery endpoint {name} must be an absolute http or https URI";
            return false;
        }

        return true;
    }

    private static bool IsHttpUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private (HttpClient Client, bool Dispose) CreateOutboundHttpClient(TimeSpan timeout)
    {
        if (_configuration.GetValue<bool>("Testing:AllowLocalExternalOidcHttp"))
        {
            var client = _httpFactory.CreateClient();
            return (client, false);
        }

        return (NetworkSecurity.CreateSafeHttpClient(timeout), true);
    }
}
