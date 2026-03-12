using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Persistence.Extensions;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.Auth.Services;

public record AuthorizeRequestResolution(
    AuthorizeRequest? Request,
    string? ClientId,
    string? ClientBucket,
    string Mode, // "query", "par", "jar"
    bool IsValid,
    string? Error,
    string? ErrorDescription,
    int RequestSize,
    string? ParId = null
);

/// <summary>
/// Service for resolving authorization requests from various sources (query, PAR, JAR).
/// </summary>
public interface IAuthorizeRequestResolver
{
    /// <summary>
    /// Resolves an authorization request.
    /// </summary>
    /// <param name="queryParams">The query parameters from the request.</param>
    /// <param name="requestUriRaw">The request_uri parameter if present.</param>
    /// <param name="roJwtFromQuery">The request parameter (JWT) if present.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolution result.</returns>
    Task<AuthorizeRequestResolution> ResolveAsync(
        IEnumerable<KeyValuePair<string, string>> queryParams,
        string? requestUriRaw,
        string? roJwtFromQuery,
        string issuer,
        CancellationToken ct = default);
}

public sealed class AuthorizeRequestResolver(
    IRequestObjectValidator requestObjects,
    IPushedAuthorizationRequestStore parStore,
    AuthDbContext db,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthorizeRequestResolver> logger) : IAuthorizeRequestResolver
{
    public async Task<AuthorizeRequestResolution> ResolveAsync(
        IEnumerable<KeyValuePair<string, string>> queryParams,
        string? requestUriRaw,
        string? roJwtFromQuery,
        string issuer,
        CancellationToken ct = default)
    {
        var query = queryParams.ToDictionary(x => x.Key, x => x.Value);
        
        // Compute initial client bucket from query
        string? rawClientId = query.TryGetValue(OAuthConstants.Parameters.ClientId, out var cid) ? cid : null;
        string clientBucket = string.IsNullOrEmpty(rawClientId) ? "unknown" : Bucketization.BucketizeClientId(rawClientId);
        string mode = "query";
        
        // Calculate request size (approx)
        int requestSize = 0;
        foreach(var kvp in query)
        {
             requestSize += Encoding.UTF8.GetByteCount(kvp.Key) + Encoding.UTF8.GetByteCount(kvp.Value) + 1; // +1 for = or &
        }

        // JAR size check
        var maxBytes = authOptions.Value.RequestObjectMaxBytes;
        if (!string.IsNullOrEmpty(roJwtFromQuery))
        {
            if (maxBytes > 0 && Encoding.UTF8.GetByteCount(roJwtFromQuery) > maxBytes)
            {
                logger.LogWarning("JAR size too large client={Client}", clientBucket);
                return new AuthorizeRequestResolution(null, rawClientId, clientBucket, mode, false, "invalid_request", "request object too large", requestSize);
            }
        }

        // PAR resolution
        string? parId = ExtractParId(requestUriRaw);
        bool isPar = !string.IsNullOrEmpty(parId);
        if (isPar)
        {
            mode = "par";
        }

        // JAR validation
        string? requestJwt = roJwtFromQuery;
        AuthorizeRequest? jarRequest = null;
        string? jarClientId = null;

        if (!string.IsNullOrEmpty(requestJwt))
        {
            mode = isPar ? "par" : "jar";
            var aud = issuer.TrimEnd('/') + "/authorize";
            var validation = await requestObjects.ValidateAsync(requestJwt, aud);
            if (!validation.IsValid)
            {
                logger.LogWarning("Invalid request object client={Client} reason={Reason}", clientBucket, validation.Error ?? "invalid_request_object");
                return new AuthorizeRequestResolution(null, rawClientId, clientBucket, mode, false, validation.Error ?? "invalid_request", validation.ErrorDescription ?? "Invalid request object", requestSize);
            }
            jarRequest = validation.Request;
            jarClientId = validation.ClientId;

            if (!string.IsNullOrEmpty(jarClientId)) clientBucket = Bucketization.BucketizeClientId(jarClientId);

            // Require PAR check
            var requirePar = authOptions.Value.RequirePar || (jarClientId is not null && authOptions.Value.RequireParClients.Contains(jarClientId, StringComparer.Ordinal));
            if (requirePar && !isPar)
            {
                logger.LogWarning("PAR required client={Client}", clientBucket);
                return new AuthorizeRequestResolution(null, jarClientId, clientBucket, mode, false, "invalid_request", "PAR required for this client", requestSize);
            }
        }

        AuthorizeRequest effectiveReq;
        if (isPar)
        {
            var entry = parStore.TryGetById(parId!);
            if (entry is null)
            {
                logger.LogWarning("Invalid or expired request_uri client={Client}", clientBucket);
                return new AuthorizeRequestResolution(null, rawClientId, clientBucket, mode, false, "invalid_request", "Invalid or expired request_uri", requestSize);
            }
            if (!string.IsNullOrEmpty(entry.ClientId)) clientBucket = Bucketization.BucketizeClientId(entry.ClientId);
            effectiveReq = entry.Request;
            
            // State override from query
            if (query.TryGetValue(OAuthConstants.Parameters.State, out var stateFromQuery) && !string.IsNullOrEmpty(stateFromQuery))
            {
                effectiveReq = effectiveReq with { state = stateFromQuery };
            }
        }
        else if (jarRequest is not null)
        {
            // Merge query params into jarRequest
            var qp = MapQueryToRequest(query);

            // client_id must match
            if (!string.IsNullOrEmpty(qp.client_id) && !string.Equals(qp.client_id, jarClientId, StringComparison.Ordinal))
            {
                logger.LogWarning("client_id mismatch client={Client}", clientBucket);
                return new AuthorizeRequestResolution(null, jarClientId, clientBucket, mode, false, "invalid_request", "client_id in query does not match request object", requestSize);
            }

            // Immutability check
            if (!IsSameOrEmpty(qp.response_type, jarRequest.response_type) ||
                !IsSameOrEmpty(qp.redirect_uri, jarRequest.redirect_uri) ||
                !IsSameOrEmpty(qp.scope, jarRequest.scope) ||
                !IsSameOrEmpty(qp.nonce, jarRequest.nonce) ||
                !IsSameOrEmpty(qp.code_challenge, jarRequest.code_challenge) ||
                !IsSameOrEmpty(qp.code_challenge_method, jarRequest.code_challenge_method) ||
                !IsSameOrEmpty(qp.resource, jarRequest.resource) ||
                !IsSameOrEmpty(qp.response_mode, jarRequest.response_mode))
            {
                logger.LogWarning("Immutable conflict client={Client}", clientBucket);
                return new AuthorizeRequestResolution(null, jarClientId, clientBucket, mode, false, "invalid_request", "Query parameter conflicts with immutable request object", requestSize);
            }

            effectiveReq = jarRequest;
            if (!string.IsNullOrEmpty(qp.state)) effectiveReq = effectiveReq with { state = qp.state };
        }
        else
        {
            effectiveReq = MapQueryToRequest(query);
        }

        // Default client ID resolution
        if (string.IsNullOrWhiteSpace(effectiveReq.client_id))
        {
            var defaultClientId = await db.ResolveDefaultClientIdAsync(ct);
            if (!string.IsNullOrWhiteSpace(defaultClientId))
            {
                effectiveReq = effectiveReq with { client_id = defaultClientId };
                clientBucket = Bucketization.BucketizeClientId(defaultClientId);
            }
        }

        return new AuthorizeRequestResolution(effectiveReq, effectiveReq.client_id, clientBucket, mode, true, null, null, requestSize, parId);
    }

    private static AuthorizeRequest MapQueryToRequest(Dictionary<string, string> query)
    {
        return new AuthorizeRequest(
            response_type: Get(query, OAuthConstants.Parameters.ResponseType),
            client_id: Get(query, OAuthConstants.Parameters.ClientId),
            redirect_uri: Get(query, OAuthConstants.Parameters.RedirectUri),
            scope: Get(query, OAuthConstants.Parameters.Scope),
            state: Get(query, OAuthConstants.Parameters.State),
            nonce: Get(query, OAuthConstants.Parameters.Nonce),
            code_challenge: Get(query, OAuthConstants.Parameters.CodeChallenge),
            code_challenge_method: Get(query, OAuthConstants.Parameters.CodeChallengeMethod),
            resource: Get(query, OAuthConstants.Parameters.Resource),
            response_mode: Get(query, OAuthConstants.Parameters.ResponseMode),
            prompt: Get(query, OAuthConstants.Parameters.Prompt),
            max_age: Get(query, OAuthConstants.Parameters.MaxAge),
            id_token_hint: Get(query, OAuthConstants.Parameters.IdTokenHint),
            login_hint: Get(query, OAuthConstants.Parameters.LoginHint),
            acr_values: Get(query, OAuthConstants.Parameters.AcrValues),
            display: Get(query, OAuthConstants.Parameters.Display),
            ui_locales: Get(query, OAuthConstants.Parameters.UiLocales),
            claims: Get(query, OAuthConstants.Parameters.Claims),
            authorization_details: Get(query, OAuthConstants.Parameters.AuthorizationDetails)
        );
    }

    private static string? Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    private static string? ExtractParId(string? requestUri)
    {
        if (string.IsNullOrEmpty(requestUri)) return null;

        // Absolute URL like https://issuer/par/{id}
        if (Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Expect .../par/{id}
            if (segments.Length >= 2 && string.Equals(segments[^2], "par", StringComparison.OrdinalIgnoreCase))
            {
                return segments[^1];
            }
        }

        // URN form urn:ietf:params:oauth:request_uri:{id}
        const string urnPrefix = "urn:ietf:params:oauth:request_uri:";
        if (requestUri.StartsWith(urnPrefix, StringComparison.Ordinal))
        {
            return requestUri.Substring(urnPrefix.Length);
        }

        // Fallback: treat as id directly
        return requestUri;
    }

    private static bool IsSameOrEmpty(string? queryVal, string? jarVal)
    {
        if (string.IsNullOrEmpty(queryVal)) return true;
        return string.Equals(queryVal, jarVal, StringComparison.Ordinal);
    }
}
