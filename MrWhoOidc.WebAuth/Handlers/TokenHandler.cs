using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MrWhoOidc.WebAuth.Infrastructure;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ITokenHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class TokenHandler(OidcOptions options, ITokenService tokens, IClientStore clients, OidcMetrics metrics, IClientAssertionValidator assertions, IDPoPValidator dpop, ILogger<TokenHandler> logger) : ITokenHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var sw = Stopwatch.StartNew();
        string grantType = string.Empty;
        string outcome = "failure";
        try
        {
            if (!http.Request.HasFormContentType)
            {
                logger.LogWarning("/token invalid_request: missing form content type from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", "none"), new("outcome", "failure") });
                metrics.TokenFailures.Add(1, new TagList { new("grant_type", "none") });
                return ErrorResults.InvalidRequest();
            }

            var form = await http.Request.ReadFormAsync();
            grantType = form["grant_type"].ToString();

            var (clientId, clientSecret) = ReadClientCredentials(http);
            if (string.IsNullOrEmpty(clientId)) clientId = form["client_id"].ToString();

            if (string.IsNullOrWhiteSpace(clientId))
            {
                logger.LogWarning("/token invalid_request: missing client_id from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return ErrorResults.InvalidRequest("Missing client_id");
            }

            var clientAssertionType = form["client_assertion_type"].ToString();
            var clientAssertion = form["client_assertion"].ToString();
            var tokenEndpoint = (options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}") + "/token";

            bool authenticated = false;
            if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
            {
                authenticated = await assertions.ValidateAsync(clientId, clientAssertion, tokenEndpoint);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: private_key_jwt validation failed for client {ClientIdHash}", Bucket(clientId));
            }
            else
            {
                if (string.IsNullOrEmpty(clientSecret)) clientSecret = form["client_secret"].ToString();
                authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecret);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: secret validation failed for client {ClientIdHash}", Bucket(clientId));
            }

            if (!authenticated)
            {
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return ErrorResults.UnauthorizedClient();
            }

            // DPoP support: if a DPoP header is present, validate and capture jkt to bind tokens
            string? dpopJkt = null;
            var authzUrl = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var endpointUrl = authzUrl.TrimEnd('/') + "/token";
            if (http.Request.Headers.ContainsKey("DPoP"))
            {
                var validation = await dpop.ValidateForEndpointAsync(http, endpointUrl);
                if (!validation.Ok)
                {
                    logger.LogWarning("/token invalid_dpop_proof: reason={Reason} ip={IP}", validation.Error ?? "unknown", http.Connection.RemoteIpAddress?.ToString());
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.BadRequest(new { error = "invalid_dpop_proof" });
                }
                dpopJkt = validation.Jkt;
                logger.LogInformation("/token DPoP accepted: jkt={Jkt} ip={IP}", dpopJkt, http.Connection.RemoteIpAddress?.ToString());
            }

            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                var code = form["code"].ToString();
                var redirectUri = form["redirect_uri"].ToString();
                var codeVerifier = form["code_verifier"].ToString();
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
                {
                    logger.LogWarning("/token invalid_request: missing code or redirect_uri for client {ClientIdHash}", Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.InvalidRequest();
                }

                var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
                var (ok, payload, _, status) = await tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, clientId!, codeVerifier, issuer, dpopJkt);
                if (!ok)
                {
                    logger.LogWarning("/token authorization_code exchange failed for client {ClientIdHash}", Bucket(clientId!));
                }

                outcome = ok ? "success" : "failure";
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                if (ok) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return Results.Json(payload!, statusCode: status);
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                var refresh = form["refresh_token"].ToString();
                if (string.IsNullOrWhiteSpace(refresh))
                {
                    logger.LogWarning("/token invalid_request: missing refresh_token for client {ClientIdHash}", Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.InvalidRequest();
                }

                var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
                var (ok, payload, _, status) = await tokens.ExchangeRefreshTokenAsync(refresh, clientId!, issuer, dpopJkt);
                if (!ok)
                {
                    logger.LogWarning("/token refresh_token exchange failed for client {ClientIdHash}", Bucket(clientId!));
                }

                outcome = ok ? "success" : "failure";
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                if (ok) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return Results.Json(payload!, statusCode: status);
            }

            logger.LogWarning("/token unsupported_grant: {GrantType}", grantType);
            metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
            metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
            return ErrorResults.UnsupportedGrant();
        }
        finally
        {
            sw.Stop();
            metrics.TokenDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("grant_type", string.IsNullOrEmpty(grantType) ? "none" : grantType), new("outcome", outcome) });
        }
    }

    static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return (null, null);
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)) return (null, null);
        try
        {
            var raw = header.Substring("Basic ".Length).Trim();
            var bytes = Convert.FromBase64String(raw);
            var pair = Encoding.UTF8.GetString(bytes);
            var idx = pair.IndexOf(':');
            if (idx < 0) return (null, null);
            var id = pair[..idx];
            var secret = pair[(idx + 1)..];
            return (id, secret);
        }
        catch
        {
            return (null, null);
        }
    }

    static string Bucket(string clientId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
