using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

public sealed class AuthorizeRequestOrchestrator(
    IAuthorizeRequestResolver requestResolver,
    IFeatureService featureService,
    IOptions<AuthOptions> authOptions,
    OidcEndpointMetrics metrics,
    ILogger<AuthorizeRequestOrchestrator> logger) : IAuthorizeRequestOrchestrator
{
    public async Task<(IResult? error, AuthorizationContext? context)> ResolveAndValidateAsync(HttpContext http, CancellationToken ct = default)
    {
        var corr = http.GetCorrelationId();
        var tenantId = http.GetTenantId();

        // Compute initial client bucket from query (may be refined later for JAR/PAR)
        string rawClientId = http.Request.Query[OAuthConstants.Parameters.ClientId].ToString();
        string clientBucket = string.IsNullOrEmpty(rawClientId) ? "unknown" : Bucketization.BucketizeClientId(rawClientId);
        string mode = "query";

        // Record approximate request size (encoded query string length)
        var qs = http.Request.QueryString.Value ?? string.Empty;
        metrics.AuthorizeRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(qs), new TagList { new("client", clientBucket), new("mode", mode) });
        metrics.AuthorizeRequests.Add(1, new TagList { new("client", clientBucket), new("mode", mode) });

        string? requestUriRaw = http.Request.Query[OAuthConstants.Parameters.RequestUri];
        if (!string.IsNullOrEmpty(requestUriRaw))
        {
            if (!await IsFeatureEnabledAsync(FeatureFlags.AdvancedSecurity, tenantId, ct))
            {
                logger.LogWarning("/authorize 403: PAR requires advanced_security feature corr={Corr} tenant={Tenant}", corr, tenantId?.ToString() ?? "platform");
                return (ErrorResults.AccessDenied("Pushed authorization requests require an advanced security license.", correlationId: corr), null);
            }
        }

        // Optional: max request object size for query param 'request'
        var roJwtFromQuery = http.Request.Query[OAuthConstants.Parameters.Request].ToString();
        var maxBytes = authOptions.Value.RequestObjectMaxBytes;
        if (!string.IsNullOrEmpty(roJwtFromQuery))
        {
            if (maxBytes > 0 && Encoding.UTF8.GetByteCount(roJwtFromQuery) > maxBytes)
            {
                logger.LogWarning("/authorize 400: JAR size too large corr={Corr} client={Client}", corr, clientBucket);
                return (ErrorResults.InvalidRequest($"request object too large (corr={corr})"), null);
            }
            metrics.JarRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(roJwtFromQuery), new TagList { new("client", clientBucket) });
            if (!await IsFeatureEnabledAsync(FeatureFlags.AdvancedSecurity, tenantId, ct))
            {
                logger.LogWarning("/authorize 403: JAR requires advanced_security feature corr={Corr} tenant={Tenant}", corr, tenantId?.ToString() ?? "platform");
                return (ErrorResults.AccessDenied("JWT request objects require an advanced security license.", correlationId: corr), null);
            }
        }

        // Resolve request object (Query, PAR, JAR)
        var issuer = http.GetIssuer();
        var resolution = await requestResolver.ResolveAsync(
            http.Request.Query.Select(x => new KeyValuePair<string, string>(x.Key, x.Value.ToString())),
            requestUriRaw,
            roJwtFromQuery,
            issuer,
            ct);

        clientBucket = resolution.ClientBucket ?? clientBucket;
        mode = resolution.Mode;

        if (!resolution.IsValid)
        {
            if (resolution.Mode == "jar" || resolution.Mode == "par")
            {
                metrics.JarInvalid.Add(1, new TagList { new("client", clientBucket) });
            }
            logger.LogWarning("/authorize 400: resolution failed corr={Corr} client={Client} error={Error}", corr, clientBucket, resolution.Error);
            return (ErrorResults.InvalidRequest($"{resolution.ErrorDescription} (corr={corr})"), null);
        }

        if (resolution.Mode == "jar" || resolution.Mode == "par")
        {
            metrics.JarValid.Add(1, new TagList { new("client", clientBucket) });
        }

        var effectiveReq = resolution.Request!;

        return (null, new AuthorizationContext(effectiveReq, corr, clientBucket, mode, requestUriRaw));
    }

    private async Task<bool> IsFeatureEnabledAsync(string feature, Guid? tenantId, CancellationToken ct)
    {
        var enabled = await featureService.IsFeatureEnabledAsync(feature, tenantId, ct).ConfigureAwait(false);
        if (enabled)
        {
            try
            {
                await featureService.RecordFeatureUsageAsync(feature, tenantId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to record feature usage {Feature} for tenant {TenantId}", feature, tenantId);
            }
        }
        return enabled;
    }
}
