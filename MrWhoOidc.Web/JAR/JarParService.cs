using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Text.Json;

namespace MrWhoOidc.Web.JAR;

public sealed class JarParService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<JarParService> logger)
{
    public async Task<string> CreateParAsync(
        string authority,
        string clientId,
        string redirectUri,
        string scope,
        string state,
        string nonce,
        string codeChallenge,
        string codeChallengeMethod,
        string? resource,
        CancellationToken ct = default)
    {
        var privateJwk = config["Oidc:PrivateJwk"] ?? config["OIDC:PrivateJwk"];
        var clientSecret = config["Oidc:ClientSecret"] ?? config["OIDC:ClientSecret"];
        var authMode = (config["Oidc:JarAuthMode"] ?? "private_key_jwt").ToLowerInvariant();

        var usePrivateKeyJwt = authMode == "private_key_jwt" && !string.IsNullOrWhiteSpace(privateJwk);
        var useClientSecretPost = authMode == "client_secret_post";
        var useClientSecretBasic = authMode == "client_secret_basic";

        if (!usePrivateKeyJwt && string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Configure either Oidc:PrivateJwk for private_key_jwt or Oidc:ClientSecret with Oidc:JarAuthMode=client_secret_post|client_secret_basic.");
        }

        // Discover exact endpoints to avoid audience mismatches
        var (authorizationEndpoint, parEndpoint) = await FetchEndpointsAsync(authority, ct);
        logger.LogInformation("Using endpoints: authorization={AuthorizationEndpoint}, par={ParEndpoint}", authorizationEndpoint, parEndpoint);

        var now = DateTimeOffset.UtcNow;

        // Build JAR request object payload
        var roPayload = new Dictionary<string, object?>
        {
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["aud"] = authorizationEndpoint,
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-30).ToUnixTimeSeconds(),
            // OIDC parameters
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = codeChallengeMethod,
            ["resource"] = resource
        };

        // Always send signed request object for JAR
        var requestJwt = SignJwt(privateJwk ?? string.Empty, roPayload);

        var form = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["request"] = requestJwt
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, parEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        if (usePrivateKeyJwt)
        {
            // private_key_jwt client assertion for PAR
            var assertionPayload = new Dictionary<string, object?>
            {
                ["iss"] = clientId,
                ["sub"] = clientId,
                ["aud"] = parEndpoint,
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds()
            };
            var clientAssertion = SignJwt(privateJwk!, assertionPayload);
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = clientAssertion;
            msg.Content = new FormUrlEncodedContent(form);
            logger.LogInformation("PAR auth mode: private_key_jwt");
        }
        else if (useClientSecretPost)
        {
            form["client_secret"] = clientSecret;
            msg.Content = new FormUrlEncodedContent(form);
            logger.LogInformation("PAR auth mode: client_secret_post");
        }
        else if (useClientSecretBasic)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}");
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            logger.LogInformation("PAR auth mode: client_secret_basic");
        }

        var http = httpFactory.CreateClient("OidcBackchannel");
        using var resp = await http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Try extract error from JSON
            string detail = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                var ed = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrEmpty(err) || !string.IsNullOrEmpty(ed))
                    detail = $"{err}: {ed}";
            }
            catch(Exception ex)
            {
                logger.LogDebug(ex, "PAR: error parsing error response JSON");
            }

            logger.LogError("PAR failed: {Status} {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"PAR request failed: {(int)resp.StatusCode} {detail}");
        }
        using var doc2 = JsonDocument.Parse(body);
        var requestUri = doc2.RootElement.TryGetProperty("request_uri", out var ru) ? ru.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestUri))
            throw new InvalidOperationException("PAR response missing request_uri");
        return requestUri!;
    }

    private async Task<(string AuthorizationEndpoint, string ParEndpoint)> FetchEndpointsAsync(string authority, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("OidcBackchannel");
        var wellKnown = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        using var resp = await http.GetAsync(wellKnown, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var authz = root.GetProperty("authorization_endpoint").GetString()!;
        var par = root.TryGetProperty("pushed_authorization_request_endpoint", out var parProp) ? parProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(par)) par = authority.TrimEnd('/') + "/par";
        return (authz, par!);
    }

    private static string SignJwt(string privateJwkJson, IDictionary<string, object?> payload)
    {
        var jwk = string.IsNullOrWhiteSpace(privateJwkJson) ? null : new JsonWebKey(privateJwkJson);
        if (jwk is null)
        {
            // Make unsigned token (will be rejected by server). Useful to get explicit error text.
            var unsignedHeader = new JwtHeader();
            var unsignedPayload = new JwtPayload();
            foreach (var kv in payload.Where(kv => kv.Value is not null))
                unsignedPayload[kv.Key] = kv.Value;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(unsignedHeader, unsignedPayload));
        }

        var alg = !string.IsNullOrEmpty(jwk.Alg) ? jwk.Alg :
                  jwk.Kty == "EC" ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256;

        var creds = new SigningCredentials(jwk, alg);
        var header = new JwtHeader(creds);
        if (!string.IsNullOrEmpty(jwk.Kid)) header["kid"] = jwk.Kid;

        var jwtPayload = new JwtPayload();
        foreach (var kv in payload.Where(kv => kv.Value is not null))
            jwtPayload[kv.Key] = kv.Value;

        var token = new JwtSecurityToken(header, jwtPayload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
