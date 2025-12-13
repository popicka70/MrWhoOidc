using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Result of building an authorization request.
/// </summary>
public sealed class AuthorizationRequestResult
{
    public required string RedirectUrl { get; init; }
    public required string Mechanism { get; init; } // "par", "jar", "query"
}

/// <summary>
/// Builds OIDC authorization requests including JAR and PAR support.
/// </summary>
public interface IExternalOidcRequestBuilder
{
    Task<AuthorizationRequestResult> BuildAuthorizationRequestAsync(
        HttpContext http,
        IdentityProvider provider,
        OidcProviderConfig config,
        DiscoveryResponse discovery,
        string state,
        string nonce,
        string codeChallenge,
        string returnUrl);
}

internal sealed class ExternalOidcRequestBuilder : IExternalOidcRequestBuilder
{
    private readonly AuthDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExternalOidcRequestBuilder> _logger;

    public ExternalOidcRequestBuilder(
        AuthDbContext db,
        IHttpClientFactory httpFactory,
        ILogger<ExternalOidcRequestBuilder> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<AuthorizationRequestResult> BuildAuthorizationRequestAsync(
        HttpContext http,
        IdentityProvider provider,
        OidcProviderConfig config,
        DiscoveryResponse discovery,
        string state,
        string nonce,
        string codeChallenge,
        string returnUrl)
    {
        var callback = http.GetIssuer() + "/auth/external/callback";
        var responseType = string.IsNullOrWhiteSpace(config.ResponseType) ? OAuthConstants.ResponseTypes.Code : config.ResponseType.Trim();

        _logger.LogInformation("Building authorization request: callback={Callback}, responseType={ResponseType}, clientId={ClientId}", 
            callback, responseType, config.ClientId);

        var query = new Dictionary<string, string?>
        {
            [OAuthConstants.Parameters.ResponseType] = responseType,
            [OAuthConstants.Parameters.ClientId] = config.ClientId,
            [OAuthConstants.Parameters.RedirectUri] = callback,
            [OAuthConstants.Parameters.Scope] = string.Join(' ', config.Scopes ?? new[] { OidcConstants.Scopes.OpenId, OidcConstants.Scopes.Profile, OidcConstants.Scopes.Email }),
            [OAuthConstants.Parameters.State] = state,
            [OAuthConstants.Parameters.Nonce] = nonce,
            [OAuthConstants.Parameters.CodeChallenge] = codeChallenge,
            [OAuthConstants.Parameters.CodeChallengeMethod] = OAuthConstants.CodeChallengeMethods.S256
        };

        if (!string.IsNullOrWhiteSpace(config.ResponseMode))
        {
            query["response_mode"] = config.ResponseMode;
        }

        if (config.ExtraAuthParams is { Count: > 0 })
        {
            foreach (var kvp in config.ExtraAuthParams)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    query[kvp.Key] = kvp.Value;
                }
            }
        }

        ExternalOidcUrlHelpers.CopyHintsFromUrl(returnUrl, query);

        string? requestJwt = null;
        if (config.UseJAR)
        {
            requestJwt = await BuildJarAsync(http, provider, config, discovery, query, state, nonce);
        }

        if (config.UsePAR && !string.IsNullOrEmpty(discovery.PushedAuthorizationRequestEndpoint))
        {
            var parResult = await TryPushAuthorizationRequestAsync(
                http,
                config,
                discovery.PushedAuthorizationRequestEndpoint,
                requestJwt,
                query);

            if (parResult is not null)
            {
                _logger.LogInformation("Redirecting to authorization endpoint with PAR request_uri.");
                return new AuthorizationRequestResult
                {
                    RedirectUrl = parResult,
                    Mechanism = "par"
                };
            }
        }

        var ub = new UriBuilder(discovery.AuthorizationEndpoint);
        var q = System.Web.HttpUtility.ParseQueryString(ub.Query);

        if (!string.IsNullOrEmpty(requestJwt))
        {
            q["client_id"] = config.ClientId;
            q["request"] = requestJwt;
        }
        else
        {
            foreach (var kv in query)
            {
                if (!string.IsNullOrEmpty(kv.Value))
                    q[kv.Key] = kv.Value;
            }
        }

        ub.Query = q.ToString();

        _logger.LogInformation("Redirecting to authorization endpoint (standard).");
        return new AuthorizationRequestResult
        {
            RedirectUrl = ub.ToString(),
            Mechanism = string.IsNullOrEmpty(requestJwt) ? "query" : "jar"
        };
    }

    private async Task<string?> BuildJarAsync(
        HttpContext http,
        IdentityProvider provider,
        OidcProviderConfig config,
        DiscoveryResponse discovery,
        Dictionary<string, string?> query,
        string state,
        string nonce)
    {
        var key = await _db.IdentityProviderKeys.AsNoTracking()
            .Where(k => k.IdentityProviderId == provider.Id && k.Purpose == IdentityProviderKeyPurpose.Signing && k.Active)
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (key is null)
            return null;

        try
        {
            var jsonWebKey = new JsonWebKey(key.Jwk);
            if (!string.IsNullOrEmpty(key.Kid) && string.IsNullOrEmpty(jsonWebKey.KeyId))
            {
                jsonWebKey.KeyId = key.Kid;
            }

            var alg = MapAlgorithm(key.Alg);
            var creds = new SigningCredentials(jsonWebKey, alg);

            var aud = ((discovery.Issuer ?? config.Authority).TrimEnd('/')) + "/authorize";
            var now = DateTimeOffset.UtcNow;

            var claims = new Dictionary<string, object?>
            {
                ["response_type"] = query.GetValueOrDefault("response_type"),
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = query.GetValueOrDefault("redirect_uri"),
                ["scope"] = query.GetValueOrDefault("scope"),
                ["state"] = state,
                ["nonce"] = nonce,
                ["code_challenge"] = query.GetValueOrDefault("code_challenge"),
                ["code_challenge_method"] = query.GetValueOrDefault("code_challenge_method"),
                ["response_mode"] = query.GetValueOrDefault("response_mode"),
                ["login_hint"] = query.GetValueOrDefault("login_hint"),
                ["ui_locales"] = query.GetValueOrDefault("ui_locales"),
                ["prompt"] = query.GetValueOrDefault("prompt"),
                ["max_age"] = query.GetValueOrDefault("max_age"),
                ["acr_values"] = query.GetValueOrDefault("acr_values"),
                ["resource"] = query.GetValueOrDefault("resource")
            };

            var clean = claims.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = config.ClientId,
                Audience = aud,
                Claims = clean,
                NotBefore = now.UtcDateTime.AddMinutes(-1),
                Expires = now.AddMinutes(5).UtcDateTime,
                SigningCredentials = creds
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(descriptor);
            return handler.WriteToken(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build JAR; falling back to query");
            return null;
        }
    }

    private async Task<string?> TryPushAuthorizationRequestAsync(
        HttpContext http,
        OidcProviderConfig config,
        string parEndpoint,
        string? requestJwt,
        Dictionary<string, string?> query)
    {
        try
        {
            Dictionary<string, string> body;
            if (!string.IsNullOrEmpty(requestJwt))
            {
                body = new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId,
                    ["request"] = requestJwt
                };
            }
            else
            {
                body = query.Where(kv => !string.IsNullOrEmpty(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value!);
            }

            using var parReq = new HttpRequestMessage(HttpMethod.Post, parEndpoint)
            {
                Content = new FormUrlEncodedContent(body)
            };

            if (!string.IsNullOrEmpty(config.ClientSecret))
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}"));
                parReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }

            var httpc = _httpFactory.CreateClient();
            using var parResp = await httpc.SendAsync(parReq, http.RequestAborted);

            if (parResp.IsSuccessStatusCode)
            {
                var parBody = await parResp.Content.ReadAsStringAsync(http.RequestAborted);
                using var parDoc = JsonDocument.Parse(parBody);
                var requestUri = parDoc.RootElement.TryGetProperty("request_uri", out var ruriEl)
                    ? ruriEl.GetString()
                    : null;

                if (!string.IsNullOrEmpty(requestUri))
                {
                    var ub = new UriBuilder(parEndpoint.Replace("/par", "").TrimEnd('/') + "/authorize");
                    var qPar = System.Web.HttpUtility.ParseQueryString(ub.Query);
                    qPar["client_id"] = config.ClientId;
                    qPar["request_uri"] = requestUri;
                    ub.Query = qPar.ToString();
                    return ub.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAR attempt failed; falling back to direct redirect");
        }

        return null;
    }

    private static string MapAlgorithm(string alg)
        => alg switch
        {
            "RS256" => SecurityAlgorithms.RsaSha256,
            "PS256" => SecurityAlgorithms.RsaSsaPssSha256,
            "ES256" => SecurityAlgorithms.EcdsaSha256,
            "ES384" => SecurityAlgorithms.EcdsaSha384,
            "ES512" => SecurityAlgorithms.EcdsaSha512,
            _ => SecurityAlgorithms.RsaSha256
        };

    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = ExternalOidcEncodingHelpers.Base64UrlEncode(
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")));
        var challenge = ExternalOidcEncodingHelpers.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return (verifier, challenge);
    }
}
