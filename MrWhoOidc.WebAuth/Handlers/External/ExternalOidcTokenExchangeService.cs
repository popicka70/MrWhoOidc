using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Result of token exchange operation.
/// </summary>
public sealed class TokenExchangeResult
{
    public bool Success { get; init; }
    public string? IdToken { get; init; }
    public string? AccessToken { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// User information extracted from ID token or userinfo endpoint.
/// </summary>
public sealed class UserInfo
{
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Acr { get; set; }
    public string[] Amrs { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Handles token exchange and userinfo fetching from external OIDC providers.
/// </summary>
public interface IExternalOidcTokenExchangeService
{
    Task<TokenExchangeResult> ExchangeCodeForTokensAsync(
        string code,
        string tokenEndpoint,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier,
        CancellationToken cancellationToken);

    Task<UserInfo> EnrichUserInfoAsync(
        UserInfo baseInfo,
        string? accessToken,
        string? userinfoEndpoint,
        CancellationToken cancellationToken);
}

internal sealed class ExternalOidcTokenExchangeService : IExternalOidcTokenExchangeService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalOidcTokenExchangeService> _logger;

    public ExternalOidcTokenExchangeService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<ExternalOidcTokenExchangeService> logger)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TokenExchangeResult> ExchangeCodeForTokensAsync(
        string code,
        string tokenEndpoint,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier
        };

        if (!string.IsNullOrEmpty(clientSecret))
            form["client_secret"] = clientSecret;

        _logger.LogInformation("Token exchange POST to {TokenEndpoint}: grant_type=authorization_code, redirect_uri={RedirectUri}, client_id={ClientId}",
            tokenEndpoint,
            redirectUri,
            clientId);

        try
        {
            var (httpc, disposeHttp) = CreateOutboundHttpClient(TimeSpan.FromSeconds(15));
            using var msg = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var _ = disposeHttp ? httpc : null;

            using var tokCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tokCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var tokResp = await httpc.SendAsync(msg, tokCts.Token);
            var body = await tokResp.Content.ReadAsStringAsync(tokCts.Token);

            if (!tokResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token exchange failed: {Status} {Body}. Sent redirect_uri: {RedirectUri}",
                    (int)tokResp.StatusCode, body, redirectUri);
                return new TokenExchangeResult
                {
                    Success = false,
                    ErrorCode = "token_exchange_failed",
                    ErrorMessage = $"Token exchange failed: {(int)tokResp.StatusCode} {body}"
                };
            }

            using var tokDoc = JsonDocument.Parse(body);
            var idToken = tokDoc.RootElement.TryGetProperty("id_token", out var idt) ? idt.GetString() : null;
            var accessToken = tokDoc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;

            return new TokenExchangeResult
            {
                Success = true,
                IdToken = idToken,
                AccessToken = accessToken
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Token exchange timeout at {Endpoint}", tokenEndpoint);
            return new TokenExchangeResult
            {
                Success = false,
                ErrorCode = "token_timeout",
                ErrorMessage = "Token exchange timed out"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token exchange error at {Endpoint}", tokenEndpoint);
            return new TokenExchangeResult
            {
                Success = false,
                ErrorCode = "token_exception",
                ErrorMessage = $"Token exchange error: {ex.Message}"
            };
        }
    }

    public async Task<UserInfo> EnrichUserInfoAsync(
        UserInfo baseInfo,
        string? accessToken,
        string? userinfoEndpoint,
        CancellationToken cancellationToken)
    {
        if (userinfoEndpoint is null || string.IsNullOrEmpty(accessToken))
            return baseInfo;

        if (!string.IsNullOrEmpty(baseInfo.Subject) &&
            !string.IsNullOrEmpty(baseInfo.Email) &&
            !string.IsNullOrEmpty(baseInfo.Name))
        {
            return baseInfo;
        }

        try
        {
            var (httpc, disposeHttp) = CreateOutboundHttpClient(TimeSpan.FromSeconds(10));
            var uiReq = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
            uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var _ = disposeHttp ? httpc : null;

            using var uiResp = await httpc.SendAsync(uiReq, cancellationToken);
            var uiBody = await uiResp.Content.ReadAsStringAsync(cancellationToken);

            if (uiResp.IsSuccessStatusCode)
            {
                using var uiDoc = JsonDocument.Parse(uiBody);
                var rootUi = uiDoc.RootElement;

                baseInfo.Subject ??= TryGetAny(rootUi, "sub", "subject", "id", "user_id", "uid", "oid", "sid");
                baseInfo.Email ??= TryGetAny(rootUi, "email", "mail", "upn");
                baseInfo.Name ??= TryGetAny(rootUi, "name", "given_name", "preferred_username", "displayName");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch userinfo from {Endpoint}", userinfoEndpoint);
        }

        if (string.IsNullOrEmpty(baseInfo.Subject) && !string.IsNullOrEmpty(accessToken))
        {
            baseInfo.Subject = TryExtractSubFromJwt(accessToken);
        }

        return baseInfo;
    }

    private static string? TryGetAny(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private string? TryExtractSubFromJwt(string jwt)
    {
        if (jwt.Count(c => c == '.') != 2)
            return null;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(jwt);
            return jwtToken.Claims.FirstOrDefault(c => c.Type == "sub")?.Value
                ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
                ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "uid")?.Value
                ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "id")?.Value
                ?? jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract sub from JWT");
            return null;
        }
    }

    private (HttpClient Client, bool Dispose) CreateOutboundHttpClient(TimeSpan timeout)
    {
        if (_configuration.GetValue<bool>("Testing:AllowLocalExternalOidcHttp"))
        {
            // Guard against accidental production enablement: only allow in Development/Staging
            var env = _configuration["ASPNETCORE_ENVIRONMENT"] ?? _configuration["Environment"];
            if (!string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(env, "Staging", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(env, "Testing", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Testing:AllowLocalExternalOidcHttp is enabled but the environment is not Development/Staging/Testing. "
                    + "This flag disables SSRF protections and must not be enabled in production.");
            }
            var client = _httpFactory.CreateClient();
            return (client, false);
        }

        return (NetworkSecurity.CreateSafeHttpClient(timeout), true);
    }
}
