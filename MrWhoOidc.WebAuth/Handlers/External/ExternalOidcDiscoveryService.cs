using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<ExternalOidcDiscoveryService> _logger;

    public ExternalOidcDiscoveryService(
        IHttpClientFactory httpFactory,
        ILogger<ExternalOidcDiscoveryService> logger)
    {
        _httpFactory = httpFactory;
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

        try
        {
            var httpc = _httpFactory.CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

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
}
