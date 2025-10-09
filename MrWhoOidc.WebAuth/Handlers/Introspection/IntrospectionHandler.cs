using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Handles OAuth 2.0 token introspection requests (RFC 7662).
/// </summary>
public sealed class IntrospectionHandler(
    OidcOptions options,
    IClientStore clientStore,
    OidcMetrics oidcMetrics,
    ClientAuthenticator authenticator,
    JwtTokenIntrospector jwtIntrospector,
    OpaqueTokenIntrospector opaqueIntrospector,
    RefreshTokenIntrospector refreshIntrospector,
    ILogger<IntrospectionHandler> logger) : IIntrospectionHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var metrics = new IntrospectionMetrics(oidcMetrics);

        // Parse request
        var (request, parseError) = await IntrospectionRequestParser.ParseAsync(http).ConfigureAwait(false);
        if (parseError is not null)
        {
            var unknownTags = "unknown".BucketizeClientId().CreateMetricTags();
            metrics.RecordActiveFalse(unknownTags);
            return parseError;
        }

        var clientBucket = request!.ClientId.BucketizeClientId();
        var tags = clientBucket.CreateMetricTags();
        metrics.RecordRequest(tags);

        // Load client
        var client = await clientStore.FindByClientIdAsync(request.ClientId).ConfigureAwait(false);
        if (client is null)
        {
            metrics.RecordActiveFalse(tags);
            return ErrorResults.UnauthorizedClient("Unknown client");
        }

        // Build context
        var issuer = http.GetIssuer(options);
        var endpoint = issuer + "/introspect";
        var context = new IntrospectionContext
        {
            Request = request,
            Client = client,
            Issuer = issuer,
            Endpoint = endpoint,
            HttpContext = http,
            ClientBucket = clientBucket,
            MetricTags = tags
        };

        // Authenticate client
        var (authenticated, authError) = await authenticator.AuthenticateAsync(context).ConfigureAwait(false);
        if (!authenticated)
        {
            metrics.RecordActiveFalse(tags);
            return authError!;
        }

        // Handle refresh token hint first if specified
        if (string.Equals(request.TokenTypeHint, OAuthConstants.TokenTypes.RefreshToken, StringComparison.Ordinal))
        {
            var (refreshResponse, refreshError) = await refreshIntrospector.IntrospectAsync(context).ConfigureAwait(false);
            if (refreshError is not null)
            {
                metrics.RecordActiveFalse(tags);
                return refreshError;
            }

            if (refreshResponse is not null)
            {
                RecordMetricsAndReturn(metrics, tags, refreshResponse, out var result);
                return result;
            }
            // Fall through to try as access token
        }

        // Try JWT introspection
        var (jwtResponse, jwtError) = await jwtIntrospector.IntrospectAsync(context).ConfigureAwait(false);
        if (jwtError is not null)
        {
            metrics.RecordActiveFalse(tags);
            return jwtError;
        }

        if (jwtResponse is not null)
        {
            RecordMetricsAndReturn(metrics, tags, jwtResponse, out var result);
            return result;
        }

        // Try opaque token introspection
        var (opaqueResponse, opaqueError) = await opaqueIntrospector.IntrospectAsync(context).ConfigureAwait(false);
        if (opaqueError is not null)
        {
            metrics.RecordActiveFalse(tags);
            return opaqueError;
        }

        if (opaqueResponse is not null)
        {
            RecordMetricsAndReturn(metrics, tags, opaqueResponse, out var result);
            return result;
        }

        // Try refresh token as fallback if hint was access_token
        if (string.Equals(request.TokenTypeHint, OAuthConstants.TokenTypes.AccessToken, StringComparison.Ordinal))
        {
            var (refreshResponse, refreshError) = await refreshIntrospector.IntrospectAsync(context).ConfigureAwait(false);
            if (refreshError is not null)
            {
                metrics.RecordActiveFalse(tags);
                return refreshError;
            }

            if (refreshResponse is not null)
            {
                RecordMetricsAndReturn(metrics, tags, refreshResponse, out var result);
                return result;
            }
        }

        // Token not found or invalid
        metrics.RecordActiveFalse(tags);
        IntrospectionAuditor.LogAudit(
            logger,
            request.ClientId,
            http.Connection.RemoteIpAddress?.ToString(),
            "inactive",
            null
        );
        return Results.Json(new { active = false });
    }

    private static void RecordMetricsAndReturn(
        IntrospectionMetrics metrics,
        KeyValuePair<string, object?>[] tags,
        Dictionary<string, object?> response,
        out IResult result)
    {
        var isActive = response.TryGetValue("active", out var activeValue) &&
                      activeValue is bool active && active;

        if (isActive)
        {
            metrics.RecordActiveTrue(tags);
        }
        else
        {
            metrics.RecordActiveFalse(tags);
        }

        result = Results.Json(response);
    }
}
