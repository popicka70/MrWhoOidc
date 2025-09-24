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

            // Fetch client once for policy checks
            var clientEntity = await clients.FindByClientIdAsync(clientId!);
            if (clientEntity is null)
            {
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return ErrorResults.UnauthorizedClient();
            }

            // mTLS check for client_credentials when configured
            if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            {
                string?[] allowedThumbprints = Array.Empty<string?>();
                if (!string.IsNullOrWhiteSpace(clientEntity.M2MMtlsThumbprintsJson))
                {
                    try
                    {
                        allowedThumbprints = System.Text.Json.JsonSerializer.Deserialize<string[]>(clientEntity.M2MMtlsThumbprintsJson) ?? Array.Empty<string>();
                    }
                    catch
                    {
                        allowedThumbprints = Array.Empty<string>();
                    }
                }
                if (allowedThumbprints.Length > 0)
                {
                    var cert = await http.Connection.GetClientCertificateAsync();
                    var presented = cert?.Thumbprint;
                    var ok = !string.IsNullOrEmpty(presented) && allowedThumbprints.Any(a => string.Equals(a, presented, StringComparison.OrdinalIgnoreCase));
                    if (!ok)
                    {
                        logger.LogWarning("/token mTLS required but missing/invalid for client {ClientIdHash}", Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                        return Results.Unauthorized();
                    }
                }
            }

            bool usedPrivateKeyJwt = false;
            bool authenticated = false;
            if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
            {
                // Enforce per-client policy for private_key_jwt
                if (!clientEntity.AllowPrivateKeyJwt)
                {
                    logger.LogWarning("/token unauthorized_client: private_key_jwt disabled for client {ClientIdHash}", Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.UnauthorizedClient();
                }

                usedPrivateKeyJwt = true;
                authenticated = await assertions.ValidateAsync(clientId!, clientAssertion, tokenEndpoint);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: private_key_jwt validation failed for client {ClientIdHash}", Bucket(clientId!));
            }
            else
            {
                if (string.IsNullOrEmpty(clientSecret)) clientSecret = form["client_secret"].ToString();

                // For client_credentials, public clients must not be accepted with empty secret
                if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(clientEntity.ClientSecretHash))
                    {
                        // Force confidential client for CC when not using private_key_jwt
                        logger.LogWarning("/token unauthorized_client: public client not allowed for client_credentials {ClientIdHash}", Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }

                    // Enforce per-client allowed methods (basic/post)
                    var usedBasic = http.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal);
                    if (usedBasic && !clientEntity.AllowClientSecretBasic)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_basic disabled for client {ClientIdHash}", Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }
                    if (!usedBasic && !clientEntity.AllowClientSecretPost)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_post disabled for client {ClientIdHash}", Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }
                }

                authenticated = await clients.ValidateClientSecretAsync(clientId!, clientSecret);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: secret validation failed for client {ClientIdHash}", Bucket(clientId!));
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

            if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            {
                // Validate required audience/resource
                var aud = form["audience"].ToString();
                var resource = form["resource"].ToString();
                if (!string.IsNullOrEmpty(aud) && !string.IsNullOrEmpty(resource) && !string.Equals(aud, resource, StringComparison.Ordinal))
                {
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.InvalidRequest("audience and resource conflict");
                }
                var audience = !string.IsNullOrEmpty(resource) ? resource : (!string.IsNullOrEmpty(aud) ? aud : "api");

                // Parse scopes
                var scopeParam = form["scope"].ToString();
                var requestedScopes = string.IsNullOrWhiteSpace(scopeParam) ? Array.Empty<string>() : scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
                var result = await tokens.CreateClientCredentialsTokenAsync(clientId!, audience, requestedScopes, issuer, dpopJkt);

                outcome = result.ok ? "success" : "failure";
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                if (result.ok) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return Results.Json(result.payload!, statusCode: result.status);
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
