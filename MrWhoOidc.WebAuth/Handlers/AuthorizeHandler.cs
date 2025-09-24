using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Options;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IAuthorizeHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class AuthorizeHandler(
    IAuthorizeService authorize,
    IAuthorizationCodeService codes,
    IConsentService consents,
    OidcMetrics metrics,
    IAuthorizationCodeMetadataStore meta,
    IPushedAuthorizationRequestStore parStore,
    IRequestObjectValidator requestObjects,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthorizeHandler> logger,
    IJwtService jwt,
    IClientStore clients,
    AuthDbContext db
) : IAuthorizeHandler
{
    private const string LastIdpCookiePrefix = ".mrwhooidc.lastidp.";

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        string outcome = "redirect";

        // Compute initial client bucket from query (may be refined later for JAR/PAR)
        string rawClientId = http.Request.Query["client_id"].ToString();
        string clientBucket = string.IsNullOrEmpty(rawClientId) ? "unknown" : BucketizeClientId(rawClientId);
        string mode = "query";

        // Record approximate request size (encoded query string length)
        var qs = http.Request.QueryString.Value ?? string.Empty;
        metrics.AuthorizeRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(qs), new TagList { new("client", clientBucket), new("mode", mode) });
        metrics.AuthorizeRequests.Add(1, new TagList { new("client", clientBucket), new("mode", mode) });

        try
        {
            // If request_uri is provided, sanitize the address bar by keeping only request_uri and selected safe hints
            string? requestUriRaw = http.Request.Query["request_uri"];
            if (!string.IsNullOrEmpty(requestUriRaw))
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "request_uri", // required for PAR
                    "state",       // allowed by RFC 9101
                    "idp",         // our custom provider selector
                    "idp_hint",    // our custom hint
                    "login_hint",  // standard hints we want to preserve visually
                    "acr_values",
                    "prompt",
                    "ui_locales",
                    "max_age"
                };

                var keys = http.Request.Query.Keys.Select(k => k.ToString());
                if (keys.Except(allowed, StringComparer.OrdinalIgnoreCase).Any())
                {
                    var baseUrl = http.Request.Path;
                    var builder = new System.Text.StringBuilder("?request_uri=");
                    builder.Append(Uri.EscapeDataString(requestUriRaw));

                    foreach (var name in allowed.Where(n => !string.Equals(n, "request_uri", StringComparison.OrdinalIgnoreCase)))
                    {
                        var val = http.Request.Query[name].ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            builder.Append('&');
                            builder.Append(name);
                            builder.Append('=');
                            builder.Append(Uri.EscapeDataString(val));
                        }
                    }

                    return Results.Redirect(baseUrl + builder.ToString());
                }
            }

            // Optional: max request object size for query param 'request'
            var roJwtFromQuery = http.Request.Query["request"].ToString();
            var maxBytes = authOptions.Value.RequestObjectMaxBytes;
            if (!string.IsNullOrEmpty(roJwtFromQuery))
            {
                if (maxBytes > 0 && Encoding.UTF8.GetByteCount(roJwtFromQuery) > maxBytes)
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 400: JAR size too large corr={Corr} client={Client}", corr, clientBucket);
                    return ErrorResults.InvalidRequest($"request object too large (corr={corr})");
                }
                metrics.JarRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(roJwtFromQuery), new TagList { new("client", clientBucket) });
            }

            // If request_uri is provided, try to resolve the pushed request and merge it
            string? requestUri = requestUriRaw;
            string? parId = ExtractParId(requestUri);
            bool isPar = !string.IsNullOrEmpty(parId);
            if (isPar)
            {
                mode = "par";
            }

            // JAR: if a signed request object is provided, validate it and use its parameters
            string? requestJwt = roJwtFromQuery;
            AuthorizeRequest? jarRequest = null;
            string? jarClientId = null;
            if (!string.IsNullOrEmpty(requestJwt))
            {
                mode = isPar ? "par" : "jar";
                var issuer = GetIssuer(http);
                var aud = issuer.TrimEnd('/') + "/authorize";
                var validation = await requestObjects.ValidateAsync(requestJwt, aud);
                if (!validation.IsValid)
                {
                    outcome = "error";
                    metrics.JarInvalid.Add(1, new TagList { new("client", clientBucket) });
                    logger.LogWarning("/authorize 400: invalid request object corr={Corr} client={Client} reason={Reason}", corr, clientBucket, validation.Error ?? "invalid_request_object");
                    return ErrorResults.InvalidRequest($"{validation.ErrorDescription ?? "Invalid request object"} (corr={corr})");
                }
                jarRequest = validation.Request;
                jarClientId = validation.ClientId;

                // Update client bucket from JAR if available
                if (!string.IsNullOrEmpty(jarClientId)) clientBucket = BucketizeClientId(jarClientId);
                metrics.JarValid.Add(1, new TagList { new("client", clientBucket) });

                // If RequirePar is enabled globally or for this client, reject direct request objects
                var requirePar = authOptions.Value.RequirePar || (jarClientId is not null && authOptions.Value.RequireParClients.Contains(jarClientId, StringComparer.Ordinal));
                if (requirePar && !isPar)
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 400: PAR required corr={Corr} client={Client}", corr, clientBucket);
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
                    logger.LogWarning("/authorize 400: invalid or expired request_uri corr={Corr} client={Client}", corr, clientBucket);
                    return ErrorResults.InvalidRequest($"Invalid or expired request_uri (corr={corr})");
                }
                if (!string.IsNullOrEmpty(entry.ClientId)) clientBucket = BucketizeClientId(entry.ClientId);
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
                    response_mode = http.Request.Query["response_mode"]
                };

                // client_id must match
                if (!string.IsNullOrEmpty(qp.client_id) && !string.Equals(qp.client_id, jarClientId, StringComparison.Ordinal))
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 400: client_id mismatch corr={Corr} client={Client}", corr, clientBucket);
                    return ErrorResults.InvalidRequest($"client_id in query does not match request object (corr={corr})");
                }

                // For each param, if query has value and it's different from request object, fail immutability
                if (!IsSameOrEmpty(qp.response_type, jarRequest.response_type) ||
                    !IsSameOrEmpty(qp.redirect_uri, jarRequest.redirect_uri) ||
                    !IsSameOrEmpty(qp.scope, jarRequest.scope) ||
                    !IsSameOrEmpty(qp.nonce, jarRequest.nonce) ||
                    !IsSameOrEmpty(qp.code_challenge, jarRequest.code_challenge) ||
                    !IsSameOrEmpty(qp.code_challenge_method, jarRequest.code_challenge_method) ||
                    !IsSameOrEmpty(qp.resource, jarRequest.resource) ||
                    !IsSameOrEmpty(qp.response_mode, jarRequest.response_mode))
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 400: immutable conflict corr={Corr} client={Client}", corr, clientBucket);
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
                    response_mode = http.Request.Query["response_mode"]
                };
            }

            // Parameter: idp and idp_hint
            var idpParam = http.Request.Query["idp"].ToString();
            var idpHint = http.Request.Query["idp_hint"].ToString();
            var prompt = http.Request.Query["prompt"].ToString();
            bool forceAccountSelection = !string.IsNullOrEmpty(prompt) && prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(p => string.Equals(p, "select_account", StringComparison.OrdinalIgnoreCase));

            var validationResult = await authorize.ValidateAsync(effectiveReq);
            if (!validationResult.IsValid)
            {
                outcome = "error";
                logger.LogWarning("/authorize 400: validation failed corr={Corr} client={Client} error={Error}", corr, clientBucket, validationResult.Error);
                if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                {
                    // If JARM requested, return a signed/encrypted error JWT instead of parameters
                    if (string.Equals(effectiveReq.response_mode, "query.jwt", StringComparison.Ordinal) || string.Equals(effectiveReq.response_mode, "form_post.jwt", StringComparison.Ordinal))
                    {
                        EncryptingCredentials? enc = await TryGetJarmEncryptingCredentialsAsync(effectiveReq.client_id);
                        var jarm = CreateJarmErrorJwt(http, jwt, effectiveReq.client_id!, validationResult.Error!, $"{validationResult.ErrorDescription} (corr={corr})", effectiveReq.state, enc);
                        return JarmRedirect(effectiveReq.redirect_uri!, effectiveReq.response_mode!, jarm);
                    }

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

            // Enforce per-client login method policy
            var clientEntity = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == validationResult.ClientId);
            bool allowLocal = clientEntity?.AllowLocalLogin ?? true;
            bool allowExternal = clientEntity?.AllowExternalIdp ?? true;
            bool allowQr = clientEntity?.AllowQrLogin ?? false;

            // Provider resolution for unauthenticated users
            if (!http.User.Identity?.IsAuthenticated ?? true)
            {
                // If explicit idp and external logins are allowed, go to external
                if (!string.IsNullOrEmpty(idpParam))
                {
                    if (!allowExternal)
                    {
                        outcome = "login";
                        var returnUrlDenied = http.Request.Path + http.Request.QueryString.ToUriComponent();
                        return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrlDenied)}");
                    }
                    var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                    // Remember last provider for this client
                    SetLastProviderCookie(http, validationResult.ClientId!, idpParam);
                    var url = $"/Auth/External/Start?provider={Uri.EscapeDataString(idpParam)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
                    return Results.Redirect(url);
                }

                // Otherwise, evaluate client mappings if external allowed
                Guid? clientGuid = null;
                if (!string.IsNullOrEmpty(validationResult.ClientId) && allowExternal)
                {
                    clientGuid = await db.Clients.AsNoTracking().Where(c => c.ClientId == validationResult.ClientId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync();
                }
                if (allowExternal && clientGuid is Guid cg)
                {
                    var providerLinks = await db.ClientIdentityProviders.AsNoTracking()
                        .Where(m => m.ClientId == cg && m.Enabled)
                        .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new { m, p })
                        .OrderBy(x => x.m.Order)
                        .Select(x => new { x.p.Name, Display = x.p.DisplayName ?? x.p.Name, x.m.IsDefaultForClient, x.m.AutoRedirectIfSingle })
                        .ToListAsync();

                    if (providerLinks.Count > 0)
                    {
                        // If idp_hint matches an available provider and account selection not forced, use it
                        if (!string.IsNullOrEmpty(idpHint) && !forceAccountSelection && !allowLocal && providerLinks.Any(pl => string.Equals(pl.Name, idpHint, StringComparison.Ordinal)))
                        {
                            var retUrlHint = http.Request.Path + http.Request.QueryString.ToUriComponent();
                            SetLastProviderCookie(http, validationResult.ClientId!, idpHint);
                            var hintUrl = $"/Auth/External/Start?provider={Uri.EscapeDataString(idpHint)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(retUrlHint)}";
                            return Results.Redirect(hintUrl);
                        }

                        // If single provider and local not allowed, auto-redirect
                        if (providerLinks.Count == 1 && providerLinks[0].AutoRedirectIfSingle && !allowLocal && !forceAccountSelection)
                        {
                            var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                            SetLastProviderCookie(http, validationResult.ClientId!, providerLinks[0].Name);
                            var url = $"/Auth/External/Start?provider={Uri.EscapeDataString(providerLinks[0].Name)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
                            return Results.Redirect(url);
                        }

                        // If multiple providers, look for last-used cookie and prefer it when not forcing account selection
                        var last = TryGetLastProviderCookie(http, validationResult.ClientId!);
                        if (!string.IsNullOrEmpty(last) && providerLinks.Any(pl => string.Equals(pl.Name, last, StringComparison.Ordinal)) && !forceAccountSelection && !allowLocal)
                        {
                            var retCookie = http.Request.Path + http.Request.QueryString.ToUriComponent();
                            var url = $"/Auth/External/Start?provider={Uri.EscapeDataString(last)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(retCookie)}";
                            return Results.Redirect(url);
                        }

                        // Otherwise render provider picker
                        var ret = http.Request.Path + http.Request.QueryString.ToUriComponent();
                        var url2 = $"/Auth/Providers/Select?client_id={Uri.EscapeDataString(validationResult.ClientId!)}&ReturnUrl={Uri.EscapeDataString(ret)}";
                        if (!string.IsNullOrEmpty(idpHint)) url2 += $"&idp_hint={Uri.EscapeDataString(idpHint)}";
                        return Results.Redirect(url2);
                    }
                }

                // QR login placeholder: if allowed and hint present, route to QR page (to be implemented)
                if (allowQr && http.Request.Query.ContainsKey("qr"))
                {
                    var ret = http.Request.Path + http.Request.QueryString.ToUriComponent();
                    return Results.Redirect($"/Auth/Qr?ReturnUrl={Uri.EscapeDataString(ret)}");
                }

                // Fallback: local login if allowed
                outcome = "login";
                var returnUrl2 = http.Request.Path + http.Request.QueryString.ToUriComponent();
                if (allowLocal)
                {
                    return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl2)}");
                }

                // If local login not allowed and no external/QR path chosen, return access_denied
                if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                {
                    var uri = new UriBuilder(effectiveReq.redirect_uri);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    query["error"] = "access_denied";
                    query["error_description"] = $"No permitted login methods for this client (corr={corr})";
                    if (!string.IsNullOrEmpty(effectiveReq.state)) query["state"] = effectiveReq.state;
                    uri.Query = query.ToString();
                    return Results.Redirect(uri.ToString());
                }
                return Results.Json(new { error = "access_denied" }, statusCode: 403);
            }

            // From here: authenticated user -> issue code
            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();

            // Enforce user must be assigned to this client (and realm)
            var client = await clients.FindByClientIdAsync(validationResult.ClientId!);
            if (client is null)
            {
                outcome = "error";
                return ErrorResults.InvalidRequest($"Unknown client (corr={corr})");
            }
            var assigned = await db.UserClientAssignments.AsNoTracking()
                .AnyAsync(a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId && a.IsActive);
            if (!assigned)
            {
                outcome = "not_assigned";
                if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                {
                    var uri = new UriBuilder(effectiveReq.redirect_uri);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    query["error"] = "access_denied";
                    query["error_description"] = $"User is not assigned to this client (corr={corr})";
                    if (!string.IsNullOrEmpty(effectiveReq.state)) query["state"] = effectiveReq.state;
                    uri.Query = query.ToString();
                    return Results.Redirect(uri.ToString());
                }
                return Results.Json(new { error = "access_denied" }, statusCode: 403);
            }

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

            // New: stash upstream identity context (idp/acr/amr) for propagation into tokens
            if (!string.IsNullOrEmpty(code))
            {
                var idp = http.User.FindFirst("idp")?.Value;
                var acr = http.User.FindFirst("acr")?.Value;
                var amrValues = http.User.Claims.Where(c => c.Type == "amr").Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
                var amr = amrValues.Length > 0 ? string.Join(' ', amrValues) : null; // store space-delimited
                meta.SetUpstream(code!, idp, acr, amr);

                // Also capture mapped claims with ext_map_* prefix
                var mapped = http.User.Claims
                    .Where(c => c.Type.StartsWith("ext_map_", StringComparison.Ordinal))
                    .ToDictionary(c => c.Type.Substring("ext_map_".Length), c => c.Value, StringComparer.Ordinal);
                if (mapped.Count > 0)
                {
                    meta.SetMappedClaims(code!, mapped);
                }
            }

            // JARM response if requested
            if (!string.IsNullOrEmpty(validationResult.ResponseMode) && (validationResult.ResponseMode == "query.jwt" || validationResult.ResponseMode == "form_post.jwt"))
            {
                EncryptingCredentials? enc = await TryGetJarmEncryptingCredentialsAsync(validationResult.ClientId);
                var jarm = CreateJarmSuccessJwt(http, jwt, validationResult.ClientId!, code!, validationResult.ResponseMode!, effectiveReq.state, enc);
                return JarmRedirect(validationResult.RedirectUri!, validationResult.ResponseMode!, jarm);
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
            var tags = new TagList { new("client", clientBucket), new("mode", mode), new("outcome", outcome) };
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }

    private async Task<EncryptingCredentials?> TryGetJarmEncryptingCredentialsAsync(string? clientId)
    {
        try
        {
            if (string.IsNullOrEmpty(clientId)) return null;
            var client = await clients.FindByClientIdAsync(clientId);
            if (client is null) return null;
            var jwks = client.PublicJwksJson;
            if (string.IsNullOrWhiteSpace(jwks)) return null;
            var set = new JsonWebKeySet(jwks);
            // Prefer keys with use=enc and RSA
            var key = set.Keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase) && string.Equals(k.Use, "enc", StringComparison.OrdinalIgnoreCase))
                   ?? set.Keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase));
            if (key is null) return null;
            var encCreds = new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256Gcm);
            return encCreds;
        }
        catch
        {
            return null;
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

    private static string BucketizeClientId(string clientId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static string BuildLastProviderCookieName(string clientId)
        => LastIdpCookiePrefix + BucketizeClientId(clientId);

    private static void SetLastProviderCookie(HttpContext http, string clientId, string provider)
    {
        var name = BuildLastProviderCookieName(clientId);
        var opts = new Microsoft.AspNetCore.Http.CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddDays(90),
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = true,
            HttpOnly = true,
            IsEssential = true,
            Path = "/"
        };
        http.Response.Cookies.Append(name, provider, opts);
    }

    private static string? TryGetLastProviderCookie(HttpContext http, string clientId)
    {
        var name = BuildLastProviderCookieName(clientId);
        if (http.Request.Cookies.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    private static IResult JarmRedirect(string redirectUri, string responseMode, string jarmJwt)
    {
        if (string.Equals(responseMode, "query.jwt", StringComparison.Ordinal))
        {
            var uri = new UriBuilder(redirectUri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query.Remove("code");
            query.Remove("state");
            query["response"] = jarmJwt;
            uri.Query = query.ToString();
            return Results.Redirect(uri.ToString());
        }
        if (string.Equals(responseMode, "form_post.jwt", StringComparison.Ordinal))
        {
            var html = $"<html><body onload=\"document.forms[0].submit()\"><form method=\"post\" action=\"{System.Web.HttpUtility.HtmlAttributeEncode(redirectUri)}\"><input type=\"hidden\" name=\"response\" value=\"{System.Web.HttpUtility.HtmlAttributeEncode(jarmJwt)}\" /></form></body></html>";
            return Results.Content(html, "text/html; charset=utf-8");
        }
        // Fallback: shouldn't happen
        var uri2 = new UriBuilder(redirectUri);
        var query2 = System.Web.HttpUtility.ParseQueryString(uri2.Query);
        query2["response"] = jarmJwt;
        uri2.Query = query2.ToString();
        return Results.Redirect(uri2.ToString());
    }

    private static string CreateJarmSuccessJwt(HttpContext http, IJwtService jwt, string clientId, string code, string responseMode, string? state, EncryptingCredentials? enc)
    {
        var issuer = GetIssuer(http);
        var claims = new List<System.Security.Claims.Claim>
        {
            new("code", code)
        };
        // c_hash per JARM
        var cHash = TokenHashing.ComputeLeftHalfBase64Url(code);
        claims.Add(new("c_hash", cHash));
        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new("state", state));
            var sHash = TokenHashing.ComputeLeftHalfBase64Url(state);
            claims.Add(new("s_hash", sHash));
        }
        var exp = DateTimeOffset.UtcNow.AddMinutes(5);
        if (enc is not null)
        {
            return jwt.CreateJwtEncrypted(issuer, clientId, claims, exp, enc);
        }
        return jwt.CreateJwt(issuer, clientId, claims, exp);
    }

    private static string CreateJarmErrorJwt(HttpContext http, IJwtService jwt, string clientId, string error, string errorDescription, string? state, EncryptingCredentials? enc)
    {
        var issuer = GetIssuer(http);
        var claims = new List<System.Security.Claims.Claim>
        {
            new("error", error),
            new("error_description", errorDescription)
        };
        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new("state", state));
            var sHash = TokenHashing.ComputeLeftHalfBase64Url(state);
            claims.Add(new("s_hash", sHash));
        }
        var exp = DateTimeOffset.UtcNow.AddMinutes(5);
        if (enc is not null)
        {
            return jwt.CreateJwtEncrypted(issuer, clientId, claims, exp, enc);
        }
        return jwt.CreateJwt(issuer, clientId, claims, exp);
    }
}
