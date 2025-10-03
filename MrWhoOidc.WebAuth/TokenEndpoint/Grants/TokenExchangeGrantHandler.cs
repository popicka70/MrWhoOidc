using System.Diagnostics;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Strategy for RFC 8693 Token Exchange.
/// Mirrors prior inline implementation; future: externalize rate limit.
/// </summary>
public sealed class TokenExchangeGrantHandler(IOptions<AuthOptions> authOptions,
    IDPoPValidator dpop,
    ITokenMetricsRecorder metrics,
    ITokenExchangeRateLimiter rateLimiter,
    ILogger<TokenExchangeGrantHandler> logger) : ITokenGrantHandler
{

    public string GrantType => "urn:ietf:params:oauth:grant-type:token-exchange";

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new(false, false, null);

        var http = context.Http;
        var form = context.Form;
        var client = context.ClientEntity; // already loaded
        var clientId = context.ClientId;
        var usedPrivateKeyJwt = context.UsedPrivateKeyJwt;

        // Feature flag
        if (!authOptions.Value.EnableTokenExchange)
        {
            return new(true, false, ErrorResults.UnsupportedGrant());
        }

        if (!usedPrivateKeyJwt && string.IsNullOrEmpty(client?.ClientSecretHash))
        {
            logger.LogWarning("/token unauthorized_client: public client not allowed for token-exchange {ClientIdHash}", Bucketization.Bucket(clientId));
            return new(true, false, ErrorResults.UnauthorizedClient());
        }

        // Externalized rate limiting
        var clientBucket = Bucketization.Bucket(clientId);
        var rl = await rateLimiter.ShouldAllowAsync(clientBucket, http.RequestAborted);
        if (!rl.Allowed)
        {
            if (rl.RetryAfterSeconds.HasValue)
                http.Response.Headers["Retry-After"] = rl.RetryAfterSeconds.Value.ToString();
            metrics.RecordTokenExchangeRateLimitBlocked(clientBucket, rl.RetryAfterSeconds);
            metrics.RecordTokenExchangeFailure(clientBucket, null, client?.OboDpopMode?.ToString() ?? "unknown", "unknown", "rate_limited");
            return new(true, false, Results.Json(new { error = "rate_limit_exceeded", error_description = "Too many token_exchange requests" }, statusCode: 429));
        }
        else
        {
            metrics.RecordTokenExchangeRateLimitAllowed(clientBucket);
        }

        var subjectToken = form["subject_token"].ToString();
        var subjectTokenType = form["subject_token_type"].ToString();
        var requestedTokenType = form["requested_token_type"].ToString();
        var audience = form["audience"].ToString();
        var resource = form["resource"].ToString();
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            metrics.RecordTokenExchangeFailure(clientBucket, null, client?.OboDpopMode?.ToString() ?? "unknown", "unknown", "missing_subject_token");
            return new(true, false, ErrorResults.InvalidRequest("Missing subject_token"));
        }
        if (!string.IsNullOrEmpty(audience) && !string.IsNullOrEmpty(resource) && !string.Equals(audience, resource, StringComparison.Ordinal))
        {
            metrics.RecordTokenExchangeFailure(clientBucket, null, client?.OboDpopMode?.ToString() ?? "unknown", "unknown", "aud_resource_conflict");
            return new(true, false, ErrorResults.InvalidRequest("audience and resource conflict"));
        }
        var target = !string.IsNullOrEmpty(resource) ? resource : audience;
        var scopeParam = form["scope"].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam) ? Array.Empty<string>() : scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // DPoP ATH validation for token-exchange
        string? dpopJkt = context.DPoPJkt; // earlier early validation if any (should have been skipped for TE)
        var endpointUrl = http.GetIssuer(context.Options).TrimEnd('/') + "/token";
        if (http.Request.Headers.ContainsKey("DPoP"))
        {
            var (ok, jkt) = await Infrastructure.DpopValidationHelper.ValidateForTokenEndpointAsync(dpop, http, endpointUrl, subjectToken, logger);
            if (!ok)
            {
                http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                metrics.RecordTokenExchangeFailure(clientBucket, target is null ? null : Bucketization.BucketizeAudience(target), client?.OboDpopMode?.ToString() ?? "unknown", InferSourceTokenType(subjectTokenType, subjectToken), "invalid_dpop_proof");
                return new(true, false, Results.BadRequest(new { error = "invalid_dpop_proof" }));
            }
            dpopJkt = jkt;
        }

        var issuer = http.GetIssuer(context.Options);
        var sw = Stopwatch.StartNew();
        var result = await context.Tokens.ExchangeTokenAsync(subjectToken, subjectTokenType, requestedTokenType, target, requestedScopes, clientId, issuer, dpopJkt);
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
                        metrics.RecordTokenExchangeFailure(clientBucket, target is null ? null : Bucketization.BucketizeAudience(target), client?.OboDpopMode?.ToString() ?? "unknown", InferSourceTokenType(subjectTokenType, subjectToken), "invalid_dpop_proof");
                        return new(true, false, Results.BadRequest(new { error = "invalid_dpop_proof" }));
                    }
                }
            }
            catch { }
        }

        sw.Stop();
        var outcome = result.ok ? "success" : "failure";
        var targetBucket = string.IsNullOrWhiteSpace(target) ? "none" : Bucketization.BucketizeAudience(target);
        var sourceTokenType = InferSourceTokenType(subjectTokenType, subjectToken);
        var dpopMode = client?.OboDpopMode?.ToString() ?? "unknown";
        metrics.RecordTokenExchange(outcome, clientBucket, targetBucket, dpopMode, sourceTokenType, sw.Elapsed.TotalMilliseconds);

        var corr = http.Request.Headers["x-correlation-id"].ToString();
        if (string.IsNullOrWhiteSpace(corr)) corr = http.TraceIdentifier;
        var sourceAudBucket = string.IsNullOrEmpty(subjectTokenType) && JwtLightParser.IsProbablyJwt(subjectToken) ? Bucketization.BucketizeAudience(JwtLightParser.TryGetAudience(subjectToken) ?? "none") : "none";
        logger.LogInformation("token_exchange outcome={Outcome} client={Client} source={SourceAud} target={TargetAud} dpop_mode={DpopMode} corr={CorrelationId}", outcome, clientBucket, sourceAudBucket, targetBucket, dpopMode, corr);
        return new(true, result.ok, Results.Json(result.payload!, statusCode: result.status));
    }

    private static string InferSourceTokenType(string subjectTokenType, string subjectToken)
    {
        if (!string.IsNullOrEmpty(subjectTokenType))
            return subjectTokenType.Contains("jwt", StringComparison.OrdinalIgnoreCase) ? "jwt" : "opaque";
        return JwtLightParser.IsProbablyJwt(subjectToken) ? "jwt" : "opaque";
    }
}
