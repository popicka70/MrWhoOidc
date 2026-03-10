using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Protocols;
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
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ITokenHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class TokenHandler(
    OidcOptions options,
    ITokenService tokens,
    ITokenExchangeService tokenExchange,
    IClientAuthenticator authenticator,
    IAuditSink audit,
    IDPoPValidator dpop,
    IDPoPReplayCache dpopReplayCache,
    IEnumerable<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler> grantHandlers,
    IEnumerable<MrWhoOidc.WebAuth.Observability.ITokenMetricsRecorder> tokenMetrics,
    IFeatureService featureService,
    ITenantAccessor tenantAccessor,
    ILogger<TokenHandler> logger) : ITokenHandler
{
    private readonly ITokenMetricsRecorder _metrics = tokenMetrics.FirstOrDefault() ?? new NoopTokenMetricsRecorder();
    private readonly IFeatureService _featureService = featureService;
    private readonly ITenantAccessor _tenantAccessor = tenantAccessor;
    // Token exchange per-client limiter moved into TokenExchangeGrantHandler

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        // OAuth 2.0 token responses must not be cached.
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        var sw = Stopwatch.StartNew();
        string grantType = string.Empty;
        string outcome = "failure";
        try
        {
            if (!http.Request.HasFormContentType)
            {
                logger.LogWarning("/token invalid_request: missing form content type from {IP}", http.Connection.RemoteIpAddress?.ToString());
                audit.Emit("token.request.rejected_invalid_content_type", new
                {
                    grant_type = "none",
                    ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                });
                _metrics.RecordTokenRequest("none", "failure");
                _metrics.RecordTokenFailure("none");
                return ErrorResults.InvalidRequest();
            }

            var form = await http.Request.ReadFormAsync();
            grantType = form[OAuthConstants.Parameters.GrantType].ToString();

            // Authenticate Client
            var authContext = new ClientAuthenticationContext
            {
                Usage = ClientAuthenticationUsage.TokenEndpoint,
                GrantType = grantType
            };

            var authResult = await authenticator.AuthenticateAsync(http, authContext);
            if (!authResult.IsSuccess)
            {
                audit.Emit("token.client_auth.failed", new
                {
                    grant_type = string.IsNullOrWhiteSpace(grantType) ? "none" : grantType,
                    ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                });
                _metrics.RecordTokenRequest(grantType, "failure");
                _metrics.RecordTokenFailure(grantType);
                return authResult.ErrorResult!;
            }

            var clientEntity = authResult.Client!;
            var clientId = clientEntity.ClientId;
            var usedPrivateKeyJwt = authResult.Method == ClientAuthenticationMethod.PrivateKeyJwt;

            // Early DPoP validation for non-token-exchange grants
            string? dpopJkt = null;
            // Use actual request URL for DPoP validation (what client sees), not PublicBaseUrl
            var endpointUrl = http.GetEndpointUrl();
            var tenantId = _tenantAccessor.CurrentTenant?.TenantId;

            if (!string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal))
            {
                if (http.Request.Headers.ContainsKey("DPoP"))
                {
                    var (ok, jkt) = await Infrastructure.DpopValidationHelper.ValidateForTokenEndpointAsync(dpop, dpopReplayCache, http, endpointUrl, null, logger);
                    if (!ok)
                    {
                        audit.Emit("token.dpop.rejected", new
                        {
                            client_id = clientId,
                            grant_type = grantType,
                            ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                        });
                        http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                        return Results.BadRequest(new { error = "invalid_dpop_proof" });
                    }
                    dpopJkt = jkt;
                    logger.LogInformation("/token DPoP accepted: jkt={Jkt} ip={IP}", dpopJkt, http.Connection.RemoteIpAddress?.ToString());
                    await SafeRecordFeatureUsageAsync(FeatureFlags.DPoP, tenantId, http.RequestAborted).ConfigureAwait(false);
                }
            }

            // Strategy-based grant handling
            var ctxForGrants = new MrWhoOidc.WebAuth.TokenEndpoint.Grants.TokenRequestContext(http, grantType, clientId!, tenantId, form, options, tokens, tokenExchange, dpopJkt, clientEntity, usedPrivateKeyJwt);
            foreach (var handler in grantHandlers)
            {
                var gr = await handler.TryHandleAsync(ctxForGrants);
                if (gr.Handled)
                {
                    outcome = gr.Success ? "success" : "failure";
                    audit.Emit(gr.Success ? "token.grant.success" : "token.grant.failure", new
                    {
                        client_id = clientId,
                        grant_type = string.IsNullOrWhiteSpace(grantType) ? "none" : grantType,
                        used_private_key_jwt = usedPrivateKeyJwt,
                        used_dpop = !string.IsNullOrWhiteSpace(dpopJkt),
                        ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                    });
                    _metrics.RecordTokenRequest(grantType, outcome);
                    if (gr.Success) _metrics.RecordTokenSuccess(grantType); else _metrics.RecordTokenFailure(grantType);
                    return gr.Result ?? Results.StatusCode(500);
                }
            }

            // client_credentials handled by strategy now

            // token-exchange now handled by TokenExchangeGrantHandler strategy

            logger.LogWarning("/token unsupported_grant: {GrantType}", grantType);
            audit.Emit("token.grant.unsupported", new
            {
                client_id = clientId,
                grant_type = string.IsNullOrWhiteSpace(grantType) ? "none" : grantType,
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
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

    private async Task SafeRecordFeatureUsageAsync(string featureName, Guid? tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await _featureService.RecordFeatureUsageAsync(featureName, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to record feature usage for {Feature} (tenant {Tenant}).", featureName, tenantId?.ToString() ?? "platform");
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
