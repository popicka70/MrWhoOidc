using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;
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

public sealed class TokenHandler(OidcOptions options, ITokenService tokens, IClientStore clients, IClientAssertionValidator assertions, IDPoPValidator dpop, IEnumerable<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler> grantHandlers, IEnumerable<MrWhoOidc.WebAuth.Observability.ITokenMetricsRecorder> tokenMetrics, ILogger<TokenHandler> logger) : ITokenHandler
{
    private readonly ITokenMetricsRecorder _metrics = tokenMetrics.FirstOrDefault() ?? new NoopTokenMetricsRecorder();
    // Token exchange per-client limiter moved into TokenExchangeGrantHandler

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
                _metrics.RecordTokenRequest("none", "failure");
                _metrics.RecordTokenFailure("none");
                return ErrorResults.InvalidRequest();
            }

            var form = await http.Request.ReadFormAsync();
            grantType = form["grant_type"].ToString();

            var (clientId, clientSecret) = ReadClientCredentials(http);
            if (string.IsNullOrEmpty(clientId)) clientId = form["client_id"].ToString();

            if (string.IsNullOrWhiteSpace(clientId))
            {
                logger.LogWarning("/token invalid_request: missing client_id from {IP}", http.Connection.RemoteIpAddress?.ToString());
                _metrics.RecordTokenRequest(grantType, "failure");
                _metrics.RecordTokenFailure(grantType);
                return ErrorResults.InvalidRequest("Missing client_id");
            }

            var clientAssertionType = form["client_assertion_type"].ToString();
            var clientAssertion = form["client_assertion"].ToString();
            var tokenEndpoint = http.GetIssuer(options) + "/token";

            // Fetch client once for policy checks
            var clientEntity = await clients.FindByClientIdAsync(clientId!);
            if (clientEntity is null)
            {
                _metrics.RecordTokenRequest(grantType, "failure");
                _metrics.RecordTokenFailure(grantType);
                return ErrorResults.UnauthorizedClient();
            }

            // mTLS check for client_credentials when configured
            if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            {
                string?[] allowedThumbprints = Array.Empty<string?>();
                if (!string.IsNullOrWhiteSpace(clientEntity.M2MMtlsThumbprintsJson))
                {
                    try { allowedThumbprints = System.Text.Json.JsonSerializer.Deserialize<string[]>(clientEntity.M2MMtlsThumbprintsJson) ?? Array.Empty<string>(); }
                    catch { allowedThumbprints = Array.Empty<string>(); }
                }
                if (allowedThumbprints.Length > 0)
                {
                    var cert = await http.Connection.GetClientCertificateAsync();
                    var presented = cert?.Thumbprint;
                    var ok = !string.IsNullOrEmpty(presented) && allowedThumbprints.Any(a => string.Equals(a, presented, StringComparison.OrdinalIgnoreCase));
                    if (!ok)
                    {
                        logger.LogWarning("/token mTLS required but missing/invalid for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        _metrics.RecordTokenRequest(grantType, "failure");
                        _metrics.RecordTokenFailure(grantType);
                        http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                        return Results.Unauthorized();
                    }
                }
            }

            bool authenticated = false;
            bool usedPrivateKeyJwt = false;
            if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
            {
                if (!clientEntity.AllowPrivateKeyJwt)
                {
                    logger.LogWarning("/token unauthorized_client: private_key_jwt disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                    _metrics.RecordTokenRequest(grantType, "failure");
                    _metrics.RecordTokenFailure(grantType);
                    return ErrorResults.UnauthorizedClient();
                }
                usedPrivateKeyJwt = true;
                authenticated = await assertions.ValidateAsync(clientId!, clientAssertion, tokenEndpoint);
                if (!authenticated)
                {
                    logger.LogWarning("/token unauthorized_client: private_key_jwt validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                }
            }
            else
            {
                if (string.IsNullOrEmpty(clientSecret)) clientSecret = form["client_secret"].ToString();
                if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(clientEntity.ClientSecretHash))
                    {
                        logger.LogWarning("/token unauthorized_client: public client not allowed for client_credentials {ClientIdHash}", Bucketization.Bucket(clientId!));
                        _metrics.RecordTokenRequest(grantType, "failure");
                        _metrics.RecordTokenFailure(grantType);
                        return ErrorResults.UnauthorizedClient();
                    }
                    var usedBasic = http.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal);
                    if (usedBasic && !clientEntity.AllowClientSecretBasic)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_basic disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        _metrics.RecordTokenRequest(grantType, "failure");
                        _metrics.RecordTokenFailure(grantType);
                        return ErrorResults.UnauthorizedClient();
                    }
                    if (!usedBasic && !clientEntity.AllowClientSecretPost)
                    {
                        logger.LogWarning("/token unauthorized_client: client_secret_post disabled for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                        _metrics.RecordTokenRequest(grantType, "failure");
                        _metrics.RecordTokenFailure(grantType);
                        return ErrorResults.UnauthorizedClient();
                    }
                }
                authenticated = await clients.ValidateClientSecretAsync(clientId!, clientSecret);
                if (!authenticated)
                {
                    logger.LogWarning("/token unauthorized_client: secret validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId!));
                }
            }

            if (!authenticated)
            {
                _metrics.RecordTokenRequest(grantType, "failure");
                _metrics.RecordTokenFailure(grantType);
                return ErrorResults.UnauthorizedClient();
            }

            // Early DPoP validation for non-token-exchange grants
            string? dpopJkt = null;
            var authzUrl = http.GetIssuer(options);
            var endpointUrl = authzUrl.TrimEnd('/') + "/token";
            if (!string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal))
            {
                if (http.Request.Headers.ContainsKey("DPoP"))
                {
                    var (ok, jkt) = await Infrastructure.DpopValidationHelper.ValidateForTokenEndpointAsync(dpop, http, endpointUrl, null, logger);
                    if (!ok)
                    {
                        http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                        return Results.BadRequest(new { error = "invalid_dpop_proof" });
                    }
                    dpopJkt = jkt;
                    logger.LogInformation("/token DPoP accepted: jkt={Jkt} ip={IP}", dpopJkt, http.Connection.RemoteIpAddress?.ToString());
                }
            }

            // Strategy-based grant handling
            var ctxForGrants = new MrWhoOidc.WebAuth.TokenEndpoint.Grants.TokenRequestContext(http, grantType, clientId!, form, options, tokens, dpopJkt, clientEntity, usedPrivateKeyJwt);
            foreach (var handler in grantHandlers)
            {
                var gr = await handler.TryHandleAsync(ctxForGrants);
                if (gr.Handled)
                {
                    outcome = gr.Success ? "success" : "failure";
                    _metrics.RecordTokenRequest(grantType, outcome);
                    if (gr.Success) _metrics.RecordTokenSuccess(grantType); else _metrics.RecordTokenFailure(grantType);
                    return gr.Result ?? Results.StatusCode(500);
                }
            }

            // client_credentials handled by strategy now

            // token-exchange now handled by TokenExchangeGrantHandler strategy

            logger.LogWarning("/token unsupported_grant: {GrantType}", grantType);
            _metrics.RecordTokenRequest(grantType, "failure");
            _metrics.RecordTokenFailure(grantType);
            return ErrorResults.UnsupportedGrantType();
        }
        finally
        {
            sw.Stop();
            _metrics.RecordTokenDuration(string.IsNullOrEmpty(grantType) ? "none" : grantType, outcome, sw.Elapsed.TotalMilliseconds);
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
    private sealed class NoopTokenMetricsRecorder : ITokenMetricsRecorder
    {
        public void RecordTokenRequest(string grantType, string outcome) { }
        public void RecordTokenSuccess(string grantType) { }
        public void RecordTokenFailure(string grantType) { }
        public void RecordTokenDuration(string grantType, string outcome, double ms) { }
        public void RecordTokenExchange(string outcome, string clientBucket, string targetAudBucket, string dpopMode, string sourceTokenType, double? durationMs = null) { }
        public void RecordTokenExchangeFailure(string clientBucket, string? targetAudBucket, string dpopMode, string sourceTokenType, string reason) { }
        public void RecordTokenExchangeRateLimitAllowed(string clientBucket) { }
        public void RecordTokenExchangeRateLimitBlocked(string clientBucket, int? retryAfterSeconds) { }
    }
}
