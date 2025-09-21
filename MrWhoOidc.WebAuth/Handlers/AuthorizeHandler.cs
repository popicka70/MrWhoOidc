using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Options;
using System.Text;
using System.Diagnostics;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IAuthorizeHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class AuthorizeHandler(IAuthorizeService authorize, IAuthorizationCodeService codes, IConsentService consents, OidcMetrics metrics, IAuthorizationCodeMetadataStore meta, IPushedAuthorizationRequestStore parStore, IRequestObjectValidator requestObjects, IOptions<AuthOptions> authOptions) : IAuthorizeHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        string outcome = "redirect";
        try
        {
            metrics.AuthorizeRequests.Add(1);

            // If request_uri is provided, sanitize the address bar by keeping only request_uri and optional state
            string? requestUriRaw = http.Request.Query["request_uri"];
            if (!string.IsNullOrEmpty(requestUriRaw))
            {
                var stateRaw = http.Request.Query["state"].ToString();
                // If there are extra params beyond request_uri/state, issue a redirect to a minimal URL
                var keys = http.Request.Query.Keys.Select(k => k.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (keys.Except(new[] { "request_uri", "state" }, StringComparer.OrdinalIgnoreCase).Any())
                {
                    var baseUrl = http.Request.Path;
                    var qs = $"?request_uri={Uri.EscapeDataString(requestUriRaw)}" + (string.IsNullOrEmpty(stateRaw) ? string.Empty : $"&state={Uri.EscapeDataString(stateRaw)}");
                    return Results.Redirect(baseUrl + qs);
                }
            }

            // Optional: max request object size for query param 'request'
            var roJwtFromQuery = http.Request.Query["request"].ToString();
            var maxBytes = authOptions.Value.RequestObjectMaxBytes;
            if (!string.IsNullOrEmpty(roJwtFromQuery))
            {
                if (maxBytes > 0 && Encoding.UTF8.GetByteCount(roJwtFromQuery) > maxBytes)
                {
                    return ErrorResults.InvalidRequest($"request object too large (corr={corr})");
                }
                metrics.JarRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(roJwtFromQuery));
            }

            // If request_uri is provided, try to resolve the pushed request and merge it
            string? requestUri = requestUriRaw;
            string? parId = ExtractParId(requestUri);
            bool isPar = !string.IsNullOrEmpty(parId);

            // JAR: if a signed request object is provided, validate it and use its parameters
            string? requestJwt = roJwtFromQuery;
            AuthorizeRequest? jarRequest = null;
            string? jarClientId = null;
            if (!string.IsNullOrEmpty(requestJwt))
            {
                var issuer = GetIssuer(http);
                var aud = issuer.TrimEnd('/') + "/authorize";
                var validation = await requestObjects.ValidateAsync(requestJwt, aud);
                if (!validation.IsValid)
                {
                    outcome = "error";
                    metrics.JarInvalid.Add(1);
                    return ErrorResults.InvalidRequest($"{validation.ErrorDescription ?? "Invalid request object"} (corr={corr})");
                }
                metrics.JarValid.Add(1);
                jarRequest = validation.Request;
                jarClientId = validation.ClientId;

                // If RequirePar is enabled globally or for this client, reject direct request objects
                var requirePar = authOptions.Value.RequirePar || (jarClientId is not null && authOptions.Value.RequireParClients.Contains(jarClientId, StringComparer.Ordinal));
                if (requirePar && !isPar)
                {
                    outcome = "error";
                    return ErrorResults.InvalidRequest($"PAR required for this client (corr={corr})");
                }
            }

            AuthorizeRequest effectiveReq;
            if (isPar)
            {
                var entry = parStore.TryGetById(parId!);
                if (entry is null)
                {
                    outcome = "error";
                    return ErrorResults.InvalidRequest($"Invalid or expired request_uri (corr={corr})");
                }
                effectiveReq = entry.Request;
                var stateFromQuery = http.Request.Query["state"].ToString();
                if (!string.IsNullOrEmpty(stateFromQuery)) effectiveReq.state = stateFromQuery;
            }
            else if (jarRequest is not null)
            {
                // Merge query params into jarRequest, enforcing immutability (query cannot conflict with values inside request object)
                var qp = new AuthorizeRequest
                {
                    response_type = http.Request.Query["response_type"],
                    client_id = http.Request.Query["client_id"],
                    redirect_uri = http.Request.Query["redirect_uri"],
                    scope = http.Request.Query["scope"],
                    state = http.Request.Query["state"],
                    nonce = http.Request.Query["nonce"],
                    code_challenge = http.Request.Query["code_challenge"],
                    code_challenge_method = http.Request.Query["code_challenge_method"],
                    resource = http.Request.Query["resource"],
                };

                // client_id must match
                if (!string.IsNullOrEmpty(qp.client_id) && !string.Equals(qp.client_id, jarClientId, StringComparison.Ordinal))
                {
                    outcome = "error";
                    return ErrorResults.InvalidRequest($"client_id in query does not match request object (corr={corr})");
                }

                // For each param, if query has value and it's different from request object, fail immutability
                if (!IsSameOrEmpty(qp.response_type, jarRequest.response_type) ||
                    !IsSameOrEmpty(qp.redirect_uri, jarRequest.redirect_uri) ||
                    !IsSameOrEmpty(qp.scope, jarRequest.scope) ||
                    !IsSameOrEmpty(qp.nonce, jarRequest.nonce) ||
                    !IsSameOrEmpty(qp.code_challenge, jarRequest.code_challenge) ||
                    !IsSameOrEmpty(qp.code_challenge_method, jarRequest.code_challenge_method) ||
                    !IsSameOrEmpty(qp.resource, jarRequest.resource))
                {
                    outcome = "error";
                    return ErrorResults.InvalidRequest($"Query parameter conflicts with immutable request object (corr={corr})");
                }

                effectiveReq = jarRequest;
                // state can be supplied outside and should override if provided
                if (!string.IsNullOrEmpty(qp.state)) effectiveReq.state = qp.state;
            }
            else
            {
                effectiveReq = new AuthorizeRequest
                {
                    response_type = http.Request.Query["response_type"],
                    client_id = http.Request.Query["client_id"],
                    redirect_uri = http.Request.Query["redirect_uri"],
                    scope = http.Request.Query["scope"],
                    state = http.Request.Query["state"],
                    nonce = http.Request.Query["nonce"],
                    code_challenge = http.Request.Query["code_challenge"],
                    code_challenge_method = http.Request.Query["code_challenge_method"],
                    resource = http.Request.Query["resource"],
                };
            }

            var validationResult = await authorize.ValidateAsync(effectiveReq);
            if (!validationResult.IsValid)
            {
                outcome = "error";
                if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                {
                    var uri = new UriBuilder(effectiveReq.redirect_uri);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    query["error"] = validationResult.Error;
                    query["error_description"] = $"{validationResult.ErrorDescription} (corr={corr})";
                    if (!string.IsNullOrEmpty(effectiveReq.state)) query["state"] = effectiveReq.state;
                    uri.Query = query.ToString();
                    return Results.Redirect(uri.ToString());
                }
                return ErrorResults.InvalidRequest($"{validationResult.ErrorDescription} (corr={corr})");
            }

            if (!http.User.Identity?.IsAuthenticated ?? true)
            {
                outcome = "login";
                var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();

            if (validationResult.RequireConsent && !await consents.HasConsentAsync(userId, validationResult.ClientId!, validationResult.Scopes))
            {
                outcome = "consent";
                var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                var consentUrl = $"/consent?ClientId={Uri.EscapeDataString(validationResult.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}&" + string.Join("&", validationResult.Scopes.Select(s => $"Scopes={Uri.EscapeDataString(s)}"));
                return Results.Redirect(consentUrl);
            }

            var (ok, _, redirect, code) = await codes.IssueAsync(validationResult, userId);
            if (!ok || redirect is null) return Results.Json(new { error = "server_error" }, statusCode: 500);

            // Now that authorization succeeded, consume PAR if used
            if (isPar)
            {
                parStore.MarkConsumedById(parId!);
                metrics.ParConsumed.Add(1);
            }

            // Capture auth_time from login cookie claims
            var authTimeClaim = http.User.FindFirst("auth_time")?.Value;
            if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
            {
                meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }

            // Persist RFC 8707 resource indicator with the code (if present)
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(validationResult.Resource))
            {
                meta.SetResource(code!, validationResult.Resource!);
            }

            if (!string.IsNullOrEmpty(effectiveReq.state))
            {
                var uri = new UriBuilder(redirect);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                query["state"] = effectiveReq.state;
                uri.Query = query.ToString();
                return Results.Redirect(uri.ToString());
            }

            return Results.Redirect(redirect);
        }
        finally
        {
            sw.Stop();
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("outcome", outcome) });
        }
    }

    private static bool IsSameOrEmpty(string? queryValue, string? roValue)
    {
        if (string.IsNullOrEmpty(queryValue)) return true; // empty query is fine
        return string.Equals(queryValue, roValue, StringComparison.Ordinal);
    }

    private static string GetIssuer(HttpContext http)
        => (http.RequestServices.GetService(typeof(OidcOptions)) as OidcOptions)?.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

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
}
