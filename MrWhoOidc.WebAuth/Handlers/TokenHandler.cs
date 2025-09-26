using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MrWhoOidc.Security;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ITokenHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class TokenHandler(OidcOptions options, ITokenService tokens, IClientStore clients, OidcMetrics metrics, IClientAssertionValidator assertions, IDPoPValidator dpop, IEnumerable<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler> grantHandlers, ILogger<TokenHandler> logger) : ITokenHandler
{
    // Simple in-memory per-client limiter for token exchange; replace with distributed limiter in multi-node deployments.
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _teWindows = new();
    private const int TokenExchangeRateLimitPerMinute = 60; // TODO: make configurable

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
                        logger.LogWarning("/token mTLS required but missing/invalid for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                        return Results.Unauthorized();
                    }
                }
            }

            bool authenticated = false;
            bool usedPrivateKeyJwt = false;
            if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
            {
                // Enforce per-client policy for private_key_jwt
                if (!clientEntity.AllowPrivateKeyJwt)
                {
                    logger.LogWarning("/token unauthorized_client: private_key_jwt disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.UnauthorizedClient();
                }

                usedPrivateKeyJwt = true;
                authenticated = await assertions.ValidateAsync(clientId!, clientAssertion, tokenEndpoint);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: private_key_jwt validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId!));
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
                        logger.LogWarning("/token unauthorized_client: public client not allowed for client_credentials {ClientIdHash}", Bucketization.Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }

                    // Enforce per-client allowed methods (basic/post)
                    var usedBasic = http.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal);
                    if (usedBasic && !clientEntity.AllowClientSecretBasic)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_basic disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }
                    if (!usedBasic && !clientEntity.AllowClientSecretPost)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_post disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                        metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                        return ErrorResults.UnauthorizedClient();
                    }
                }

                authenticated = await clients.ValidateClientSecretAsync(clientId!, clientSecret);
                if (!authenticated)
                    logger.LogWarning("/token unauthorized_client: secret validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId!));
            }

            if (!authenticated)
            {
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return ErrorResults.UnauthorizedClient();
            }

            // DPoP support
            // For non-token-exchange grants, validate immediately (to bind outgoing tokens).
            // For token-exchange, we defer validation to the branch below where we enforce ATH bound to subject_token.
            string? dpopJkt = null;
            var authzUrl = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var endpointUrl = authzUrl.TrimEnd('/') + "/token";
            if (!string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal))
            {
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
            }

            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                var code = form["code"].ToString();
                var redirectUri = form["redirect_uri"].ToString();
                var codeVerifier = form["code_verifier"].ToString();
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
                {
                    logger.LogWarning("/token invalid_request: missing code or redirect_uri for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.InvalidRequest();
                }

                var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
                var (ok, payload, _, status) = await tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, clientId!, codeVerifier, issuer, dpopJkt);
                if (!ok)
                {
                    logger.LogWarning("/token authorization_code exchange failed for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                }

                outcome = ok ? "success" : "failure";
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                if (ok) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                return Results.Json(payload!, statusCode: status);
            }

            // Strategy-based grant handling (pilot: refresh_token)
            var ctxForGrants = new MrWhoOidc.WebAuth.TokenEndpoint.Grants.TokenRequestContext(http, grantType, clientId!, form, options, tokens, dpopJkt);
            foreach (var handler in grantHandlers)
            {
                var gr = await handler.TryHandleAsync(ctxForGrants);
                if (gr.Handled)
                {
                    outcome = gr.Success ? "success" : "failure";
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                    if (gr.Success) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return gr.Result ?? Results.StatusCode(500);
                }
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

            if (string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal))
            {
                // Feature flag gate
                var authOpts = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
                if (!authOpts.EnableTokenExchange)
                {
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "failure") });
                    metrics.TokenExchangeFailures.Add(1);
                    return ErrorResults.UnsupportedGrant();
                }

                // Enforce confidential client unless using private_key_jwt
                if (!usedPrivateKeyJwt && string.IsNullOrEmpty(clientEntity.ClientSecretHash))
                {
                    logger.LogWarning("/token unauthorized_client: public client not allowed for token-exchange {ClientIdHash}", Bucketization.Bucket(clientId!));
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    return ErrorResults.UnauthorizedClient();
                }

                // Per-client simple rate limit for token-exchange
                var clientBucket = Bucketization.Bucket(clientId!);
                var now = DateTimeOffset.UtcNow;
                _teWindows.AddOrUpdate(clientBucket, _ => (1, now), (_, cur) =>
                {
                    if (now - cur.WindowStart >= TimeSpan.FromMinutes(1)) return (1, now);
                    return (cur.Count + 1, cur.WindowStart);
                });
                var snapshot = _teWindows[clientBucket];
                if (snapshot.Count > TokenExchangeRateLimitPerMinute && now - snapshot.WindowStart < TimeSpan.FromMinutes(1))
                {
                    logger.LogWarning("/token 429: token-exchange per-client limit exceeded client={Client}", clientBucket);
                    metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "rate_limited") });
                    metrics.TokenExchangeFailures.Add(1);
                    return Results.Json(new { error = "rate_limit_exceeded", error_description = "Too many token_exchange requests" }, statusCode: 429);
                }

                var subjectToken = form["subject_token"].ToString();
                var subjectTokenType = form["subject_token_type"].ToString();
                var requestedTokenType = form["requested_token_type"].ToString();
                var audience = form["audience"].ToString();
                var resource = form["resource"].ToString();
                if (string.IsNullOrWhiteSpace(subjectToken))
                {
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "failure") });
                    metrics.TokenExchangeFailures.Add(1);
                    return ErrorResults.InvalidRequest("Missing subject_token");
                }
                if (!string.IsNullOrEmpty(audience) && !string.IsNullOrEmpty(resource) && !string.Equals(audience, resource, StringComparison.Ordinal))
                {
                    metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", "failure") });
                    metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });
                    metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "failure") });
                    metrics.TokenExchangeFailures.Add(1);
                    return ErrorResults.InvalidRequest("audience and resource conflict");
                }
                var target = !string.IsNullOrEmpty(resource) ? resource : audience;
                // Optional scopes requested
                var scopeParam = form["scope"].ToString();
                var requestedScopes = string.IsNullOrWhiteSpace(scopeParam) ? Array.Empty<string>() : scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Phase 2: When DPoP header is present for token-exchange, enforce ath bound to the subject_token
                if (http.Request.Headers.ContainsKey("DPoP"))
                {
                    var validationWithAth = await dpop.ValidateForEndpointAsync(http, endpointUrl, subjectToken);
                    if (!validationWithAth.Ok)
                    {
                        logger.LogWarning("/token invalid_dpop_proof (ath): reason={Reason} ip={IP}", validationWithAth.Error ?? "unknown", http.Connection.RemoteIpAddress?.ToString());
                        http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                        metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "failure"), new("reason", "invalid_dpop_proof") });
                        metrics.TokenExchangeFailures.Add(1);
                        return Results.BadRequest(new { error = "invalid_dpop_proof" });
                    }
                    // Overwrite dpopJkt with validated value (should be same as earlier validation)
                    dpopJkt = validationWithAth.Jkt;
                }

                var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
                var swTe = Stopwatch.StartNew();
                var result = await tokens.ExchangeTokenAsync(subjectToken, subjectTokenType, requestedTokenType, target, requestedScopes, clientId!, issuer, dpopJkt);
                // Normalize DPoP-related policy failures to invalid_dpop_proof for endpoint semantics expected by tests
                if (!result.ok && string.Equals(result.error, "invalid_request", StringComparison.Ordinal) && result.payload is not null)
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(result.payload);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("error_description", out var descEl))
                        {
                            var desc = descEl.GetString();
                            if (string.Equals(desc, "dpop_same_key_required", StringComparison.Ordinal) || string.Equals(desc, "dpop_bridging_not_supported", StringComparison.Ordinal))
                            {
                                http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                                metrics.TokenExchangeRequests.Add(1, new TagList { new("outcome", "failure"), new("reason", "invalid_dpop_proof") });
                                metrics.TokenExchangeFailures.Add(1);
                                return Results.BadRequest(new { error = "invalid_dpop_proof" });
                            }
                        }
                    }
                    catch { }
                }
                outcome = result.ok ? "success" : "failure";
                metrics.TokenRequests.Add(1, new TagList { new("grant_type", grantType), new("outcome", outcome) });
                if (result.ok) metrics.TokenSuccess.Add(1, new TagList { new("grant_type", grantType) }); else metrics.TokenFailures.Add(1, new TagList { new("grant_type", grantType) });

                // Dedicated TE metrics with richer tagging
                var clientBucketTag = Bucketization.Bucket(clientId!);
                var targetBucket = string.IsNullOrWhiteSpace(target) ? "none" : Bucketization.BucketizeAudience(target);
                var dpopModeTag = clientEntity?.OboDpopMode?.ToString() ?? "unknown";

                var teTags = new TagList
                {
                    new("outcome", outcome),
                    new("client_bucket", clientBucketTag),
                    new("target_aud", targetBucket),
                    new("dpop_mode", dpopModeTag),
                    new("source_token_type", string.IsNullOrEmpty(subjectTokenType) ? (JwtLightParser.IsProbablyJwt(subjectToken) ? "jwt" : "opaque") : (subjectTokenType.Contains("jwt", StringComparison.OrdinalIgnoreCase) ? "jwt" : "opaque"))
                };
                metrics.TokenExchangeRequests.Add(1, teTags);
                if (result.ok) metrics.TokenExchangeSuccess.Add(1, teTags); else metrics.TokenExchangeFailures.Add(1, teTags);
                swTe.Stop();
                metrics.TokenExchangeDurationMs.Record(swTe.Elapsed.TotalMilliseconds, teTags);

                // Structured audit log (PII-reduced)
                var corr = http.Request.Headers["x-correlation-id"].ToString();
                if (string.IsNullOrWhiteSpace(corr)) corr = http.TraceIdentifier;
                var sourceAudBucket = string.IsNullOrEmpty(subjectTokenType) && JwtLightParser.IsProbablyJwt(subjectToken) ? Bucketization.BucketizeAudience(JwtLightParser.TryGetAudience(subjectToken) ?? "none") : "none";
                logger.LogInformation("token_exchange outcome={Outcome} client={ClientBucket} source={SourceBucket} target={TargetBucket} dpop_mode={DpopMode} corr={CorrelationId}", outcome, clientBucketTag, sourceAudBucket, targetBucket, dpopModeTag, corr);
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

    // Bucketization & JWT parsing helpers moved to Infrastructure utilities.
}
