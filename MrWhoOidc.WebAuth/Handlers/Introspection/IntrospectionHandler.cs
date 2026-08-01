using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
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
    IAuditSink audit,
    OidcEndpointMetrics oidcMetrics,
    ClientAuthenticator authenticator,
    JwtTokenIntrospector jwtIntrospector,
    OpaqueTokenIntrospector opaqueIntrospector,
    RefreshTokenIntrospector refreshIntrospector,
    ILogger<IntrospectionHandler> logger) : IIntrospectionHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        var metrics = new IntrospectionMetrics(oidcMetrics);

        // Parse request
        var (request, parseError) = await IntrospectionRequestParser.ParseAsync(http).ConfigureAwait(false);
        if (parseError is not null)
        {
            audit.Emit("introspection.request.invalid", new
            {
                reason = "parse_failed",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
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
            audit.Emit("introspection.client_auth.failed", new
            {
                client_id = request.ClientId,
                reason = "client_not_found",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            metrics.RecordActiveFalse(tags);
            return ErrorResults.UnauthorizedClient("Unknown client");
        }

        // Build context
        var issuer = http.GetIssuer(options);
        // Use actual request URL for DPoP validation (what client sees), not PublicBaseUrl
        var endpoint = http.GetEndpointUrl();
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
            audit.Emit("introspection.client_auth.failed", new
            {
                client_id = request.ClientId,
                reason = "authentication_failed",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
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
                RecordMetricsAndReturn(metrics, tags, request, http, audit, client, refreshResponse, out var result);
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
            RecordMetricsAndReturn(metrics, tags, request, http, audit, client, jwtResponse, out var result);
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
            RecordMetricsAndReturn(metrics, tags, request, http, audit, client, opaqueResponse, out var result);
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
                RecordMetricsAndReturn(metrics, tags, request, http, audit, client, refreshResponse, out var result);
                return result;
            }
        }

        // Token not found or invalid
        metrics.RecordActiveFalse(tags);
        audit.Emit("introspection.result", new
        {
            client_id = request.ClientId,
            outcome = "inactive",
            audience = (string?)null,
            ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
        });
        IntrospectionAuditor.LogAudit(
            logger,
            request.ClientId,
            http.Connection.RemoteIpAddress?.ToString(),
            "inactive",
            null
        );
        return Results.Json(new { active = false });
    }

    private void RecordMetricsAndReturn(
        IntrospectionMetrics metrics,
        KeyValuePair<string, object?>[] tags,
        IntrospectionRequest request,
        HttpContext http,
        IAuditSink audit,
        Client client,
        Dictionary<string, object?> response,
        out IResult result)
    {
        var isActive = response.TryGetValue("active", out var activeValue) &&
                       activeValue is bool active && active;

        string? audience = null;
        if (response.TryGetValue("aud", out var audValue))
        {
            audience = audValue switch
            {
                string s => s,
                IEnumerable<object?> seq => string.Join(",", seq.Where(v => v is not null).Select(v => v!.ToString())),
                _ => audValue?.ToString()
            };
        }

        if (isActive)
        {
            metrics.RecordActiveTrue(tags);
        }
        else
        {
            metrics.RecordActiveFalse(tags);
        }

        // Check for delegated token (act claim present)
        if (response.TryGetValue("act", out var actValue) && actValue is not null)
        {
            var actDict = actValue switch
            {
                Dictionary<string, object?> d => d,
                _ => null
            };

            string? actorId = null;
            if (actDict is Dictionary<string, object?> actMap)
            {
                if (actMap.TryGetValue("sub", out var actSub))
                {
                    actorId = actSub switch
                    {
                        string s => s,
                        _ => actSub?.ToString()
                    };
                }
            }

            string? subjectId = null;
            if (response.TryGetValue("sub", out var subValue))
            {
                subjectId = subValue switch
                {
                    string s => s,
                    _ => subValue?.ToString()
                };
            }

            string? grantId = null;
            if (response.TryGetValue("delegation_id", out var delId))
            {
                grantId = delId switch
                {
                    string s => s,
                    _ => delId?.ToString()
                };
            }

            var tenantId = client.TenantId.ToString();

            // Emit delegated introspection audit with hashed identifiers
            audit.Emit("token_introspection.delegated", new
            {
                actor_id = audit.HashValue(actorId),
                subject_id = audit.HashValue(subjectId),
                grant_id = grantId,
                tenant_id = tenantId,
                client_id = request.ClientId,
                outcome = isActive ? "active" : "inactive",
                audience,
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });

            IntrospectionAuditor.LogAudit(
                logger,
                request.ClientId,
                http.Connection.RemoteIpAddress?.ToString(),
                isActive ? "active" : "inactive",
                audience
            );
        }
        else
        {
            var outcome = isActive ? "active" : "inactive";
            audit.Emit("introspection.result", new
            {
                client_id = request.ClientId,
                outcome,
                audience,
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            IntrospectionAuditor.LogAudit(
                logger,
                request.ClientId,
                http.Connection.RemoteIpAddress?.ToString(),
                outcome,
                audience
            );
        }

        result = Results.Json(response);
    }
}
