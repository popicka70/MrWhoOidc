using MrWhoOidc.WebAuth.Observability;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.Extensions.Options;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.Auth.Persistence.Extensions;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Services;
using Microsoft.AspNetCore.WebUtilities;

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
    IAuthorizeRequestResolver requestResolver,
    IPushedAuthorizationRequestStore parStore,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthorizeHandler> logger,
    IClientStore clients,
    AuthDbContext db,
    IQrLoginHandler qrLoginHandler,
    ITenantAccessor tenantAccessor,
    IFeatureService featureService,
    IJarmService jarm,
    ILoginContinuationStore continuationStore
) : IAuthorizeHandler
{
    private const string LastIdpCookiePrefix = ".mrwhooidc.lastidp.";

    /// <summary>
    /// Builds a tenant-aware URL by prefixing with /t/{slug} if a tenant context exists.
    /// </summary>
    private string BuildTenantAwareUrl(string path)
    {
        var currentTenant = tenantAccessor.CurrentTenant;
        
        // Ensure path starts with /
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // In multi-tenant mode with tenant context, prefix with /t/{slug}
        if (currentTenant != null && currentTenant.IsMultiTenantMode)
        {
            return $"/t/{currentTenant.Slug}{path}";
        }

        return path;
    }

    private async Task<string> BuildTenantAwareLoginUrlAsync(string returnUrl, CancellationToken cancellationToken)
    {
        var ctx = await continuationStore.StoreAsync(returnUrl, cancellationToken).ConfigureAwait(false);
        var loginPath = BuildTenantAwareUrl("/login");
        return QueryHelpers.AddQueryString(loginPath, "ctx", ctx);
    }

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        logger.LogInformation("⚡ /authorize called Path={Path}", http.Request.Path);

        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        string outcome = "redirect";
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;

        bool? advancedSecurityEnabled = null;
        var advancedSecurityRecorded = false;
        async Task<bool> EnsureAdvancedSecurityAsync()
        {
            if (advancedSecurityEnabled is null)
            {
                advancedSecurityEnabled = await featureService
                    .IsFeatureEnabledAsync(FeatureFlags.AdvancedSecurity, tenantId, http.RequestAborted)
                    .ConfigureAwait(false);
            }

            if (advancedSecurityEnabled.Value)
            {
                await RecordAdvancedSecurityUsageAsync().ConfigureAwait(false);
            }

            return advancedSecurityEnabled.Value;
        }

        async Task RecordAdvancedSecurityUsageAsync()
        {
            if (advancedSecurityRecorded)
            {
                return;
            }

            try
            {
                await featureService.RecordFeatureUsageAsync(FeatureFlags.AdvancedSecurity, tenantId, http.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to record advanced security usage for tenant {TenantId}", tenantId);
            }

            advancedSecurityRecorded = true;
        }

        // Compute initial client bucket from query (may be refined later for JAR/PAR)
        string rawClientId = http.Request.Query[OAuthConstants.Parameters.ClientId].ToString();
        string clientBucket = string.IsNullOrEmpty(rawClientId) ? "unknown" : Bucketization.BucketizeClientId(rawClientId);
        string mode = "query";

        // Record approximate request size (encoded query string length)
        var qs = http.Request.QueryString.Value ?? string.Empty;
        metrics.AuthorizeRequestSizeBytes.Record(Encoding.UTF8.GetByteCount(qs), new TagList { new("client", clientBucket), new("mode", mode) });
        metrics.AuthorizeRequests.Add(1, new TagList { new("client", clientBucket), new("mode", mode) });

        try
        {
            // If request_uri is provided, sanitize the address bar by keeping only request_uri and selected safe hints
            string? requestUriRaw = http.Request.Query[OAuthConstants.Parameters.RequestUri];
            if (!string.IsNullOrEmpty(requestUriRaw))
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    OAuthConstants.Parameters.RequestUri, // required for PAR
                    OAuthConstants.Parameters.State,       // allowed by RFC 9101
                    OidcConstants.Claims.Idp,         // our custom provider selector
                    "idp_hint",    // our custom hint
                    "qr",          // QR login flow hint
                    OidcConstants.Parameters.LoginHint,  // standard hints we want to preserve visually
                    OidcConstants.Parameters.AcrValues,
                    OidcConstants.Parameters.Prompt,
                    OidcConstants.Parameters.UiLocales,
                    OidcConstants.Parameters.MaxAge
                };

                var keys = http.Request.Query.Keys.Select(k => k.ToString());
                if (keys.Except(allowed, StringComparer.OrdinalIgnoreCase).Any())
                {
                    var baseUrl = http.Request.Path;
                    var builder = new System.Text.StringBuilder("?request_uri=");
                    builder.Append(Uri.EscapeDataString(requestUriRaw));

                    foreach (var name in allowed.Where(n => !string.Equals(n, OAuthConstants.Parameters.RequestUri, StringComparison.OrdinalIgnoreCase)))
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

            if (!string.IsNullOrEmpty(requestUriRaw))
            {
                if (!await EnsureAdvancedSecurityAsync().ConfigureAwait(false))
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 403: PAR requires advanced_security feature corr={Corr} tenant={Tenant}", corr, tenantId?.ToString() ?? "platform");
                    return ErrorResults.AccessDenied("Pushed authorization requests require an advanced security license.", correlationId: corr);
                }
            }

            // Optional: max request object size for query param 'request'
            var roJwtFromQuery = http.Request.Query[OAuthConstants.Parameters.Request].ToString();
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
                if (!await EnsureAdvancedSecurityAsync().ConfigureAwait(false))
                {
                    outcome = "error";
                    logger.LogWarning("/authorize 403: JAR requires advanced_security feature corr={Corr} tenant={Tenant}", corr, tenantId?.ToString() ?? "platform");
                    return ErrorResults.AccessDenied("JWT request objects require an advanced security license.", correlationId: corr);
                }
            }

            // Resolve request object (Query, PAR, JAR)
            var issuer = http.GetIssuer();
            var resolution = await requestResolver.ResolveAsync(
                http.Request.Query.Select(x => new KeyValuePair<string, string>(x.Key, x.Value.ToString())),
                requestUriRaw,
                roJwtFromQuery,
                issuer,
                http.RequestAborted);

            clientBucket = resolution.ClientBucket ?? clientBucket;
            mode = resolution.Mode;

            if (!resolution.IsValid)
            {
                outcome = "error";
                if (resolution.Mode == "jar" || resolution.Mode == "par")
                {
                     metrics.JarInvalid.Add(1, new TagList { new("client", clientBucket) });
                }
                logger.LogWarning("/authorize 400: resolution failed corr={Corr} client={Client} error={Error}", corr, clientBucket, resolution.Error);
                return ErrorResults.InvalidRequest($"{resolution.ErrorDescription} (corr={corr})");
            }

            if (resolution.Mode == "jar" || resolution.Mode == "par")
            {
                metrics.JarValid.Add(1, new TagList { new("client", clientBucket) });
            }

            var effectiveReq = resolution.Request!;

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
                    if (string.Equals(effectiveReq.response_mode, OidcConstants.ResponseModes.QueryJwt, StringComparison.Ordinal) || string.Equals(effectiveReq.response_mode, OidcConstants.ResponseModes.FormPostJwt, StringComparison.Ordinal))
                    {
                        var jarmJwt = await jarm.CreateErrorResponseAsync(effectiveReq.client_id!, issuer, validationResult.Error!, $"{validationResult.ErrorDescription} (corr={corr})", effectiveReq.state);
                        return JarmRedirect(effectiveReq.redirect_uri!, effectiveReq.response_mode!, jarmJwt);
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

            // DEBUG: Check QR parameter
            bool hasQrParam = http.Request.Query.ContainsKey("qr");
            logger.LogInformation("🔍 QR Check: allowQr={AllowQr}, hasQrParam={HasQr}", allowQr, hasQrParam);

            // QR login: if allowed and hint present, initiate QR flow BEFORE provider selection
            if (allowQr && http.Request.Query.ContainsKey("qr"))
            {
                logger.LogInformation("Routing to QR login for client {ClientId}, allowQr={AllowQr}", validationResult.ClientId, allowQr);
                logger.LogInformation("QR routing details: validationResult.IsValid={IsValid}, HasRedirectUri={HasRedirectUri}, ScopeCount={ScopeCount}, CodeChallenge={HasChallenge}",
                    validationResult.IsValid,
                    !string.IsNullOrEmpty(validationResult.RedirectUri),
                    validationResult.Scopes?.Length ?? 0,
                    !string.IsNullOrEmpty(validationResult.CodeChallenge));
                outcome = "qr_initiate";
                logger.LogInformation("Calling qrLoginHandler.InitiateAsync with 3 parameters (http, validationResult, effectiveReq)");
                return await qrLoginHandler.InitiateAsync(http, validationResult, effectiveReq);
            }

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
                        var loginUrl = await BuildTenantAwareLoginUrlAsync(returnUrlDenied, http.RequestAborted).ConfigureAwait(false);
                        return Results.Redirect(loginUrl);
                    }
                    var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                    // Remember last provider for this client
                    SetLastProviderCookie(http, validationResult.ClientId!, idpParam);
                    var url = $"/auth/external/start?provider={Uri.EscapeDataString(idpParam)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
                    return Results.Redirect(url);
                }

                // Otherwise, evaluate client mappings if external allowed OR if QR is enabled
                Guid? clientGuid = null;
                if (!string.IsNullOrEmpty(validationResult.ClientId) && (allowExternal || allowQr))
                {
                    clientGuid = await db.Clients.AsNoTracking().Where(c => c.ClientId == validationResult.ClientId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync();
                }

                // Load provider links if external IdPs are allowed
                var providerLinks = new List<dynamic>();
                if (allowExternal && clientGuid is Guid cg)
                {
                    providerLinks = await db.ClientIdentityProviders.AsNoTracking()
                        .Where(m => m.ClientId == cg && m.Enabled)
                        .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new { m, p })
                        .OrderBy(x => x.m.Order)
                        .Select(x => new { x.p.Name, Display = x.p.DisplayName ?? x.p.Name, x.m.IsDefaultForClient, x.m.AutoRedirectIfSingle })
                        .ToListAsync<dynamic>();
                }

                // Decide whether to show provider picker: if we have external providers OR QR is enabled
                bool shouldShowPicker = providerLinks.Count > 0 || allowQr;

                if (shouldShowPicker)
                {
                    // If idp_hint matches an available provider and account selection not forced, use it
                    if (!string.IsNullOrEmpty(idpHint) && !forceAccountSelection && !allowLocal && providerLinks.Any(pl => string.Equals(pl.Name, idpHint, StringComparison.Ordinal)))
                    {
                        var retUrlHint = http.Request.Path + http.Request.QueryString.ToUriComponent();
                        SetLastProviderCookie(http, validationResult.ClientId!, idpHint);
                        var hintUrl = $"/auth/external/start?provider={Uri.EscapeDataString(idpHint)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(retUrlHint)}";
                        return Results.Redirect(hintUrl);
                    }

                    // If single provider and local not allowed, auto-redirect
                    if (providerLinks.Count == 1 && providerLinks[0].AutoRedirectIfSingle && !allowLocal && !allowQr && !forceAccountSelection)
                    {
                        var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                        SetLastProviderCookie(http, validationResult.ClientId!, providerLinks[0].Name);
                        var url = $"/auth/external/start?provider={Uri.EscapeDataString(providerLinks[0].Name)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
                        return Results.Redirect(url);
                    }

                    // If multiple providers, look for last-used cookie and prefer it when not forcing account selection
                    var last = TryGetLastProviderCookie(http, validationResult.ClientId!);
                    logger.LogInformation("Last provider cookie check: last={LastProvider}, providerLinksCount={Count}, forceAccountSelection={Force}, allowLocal={AllowLocal}, allowQr={AllowQr}",
                        last ?? "(null)", providerLinks.Count, forceAccountSelection, allowLocal, allowQr);
                    if (!string.IsNullOrEmpty(last) && providerLinks.Any(pl => string.Equals(pl.Name, last, StringComparison.Ordinal)) && !forceAccountSelection && !allowLocal && !allowQr)
                    {
                        logger.LogWarning("⚠️ Auto-redirecting to last-used provider '{LastProvider}' due to cookie (even though providerLinks is empty). This may cause a loop!", last);
                        var retCookie = http.Request.Path + http.Request.QueryString.ToUriComponent();
                        var url = $"/auth/external/start?provider={Uri.EscapeDataString(last)}&clientId={Uri.EscapeDataString(validationResult.ClientId!)}&returnUrl={Uri.EscapeDataString(retCookie)}";
                        return Results.Redirect(url);
                    }

                    // Show provider picker (includes QR option if allowQr is true)
                    var ret = http.Request.Path + http.Request.QueryString.ToUriComponent();
                    var url2 = $"/auth/providers/select?client_id={Uri.EscapeDataString(validationResult.ClientId!)}&ReturnUrl={Uri.EscapeDataString(ret)}";
                    if (!string.IsNullOrEmpty(idpHint)) url2 += $"&idp_hint={Uri.EscapeDataString(idpHint)}";
                    logger.LogInformation("Redirecting to provider picker (allowQr={AllowQr}, providerCount={Count})", allowQr, providerLinks.Count);
                    return Results.Redirect(url2);
                }

                logger.LogDebug("QR login not triggered: allowQr={AllowQr}, hasQrParam={HasQr}", allowQr, http.Request.Query.ContainsKey("qr"));

                // Fallback: local login if allowed
                outcome = "login";
                var returnUrl2 = http.Request.Path + http.Request.QueryString.ToUriComponent();
                if (allowLocal)
                {
                    var loginUrl = await BuildTenantAwareLoginUrlAsync(returnUrl2, http.RequestAborted).ConfigureAwait(false);
                    return Results.Redirect(loginUrl);
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
                // If the client opts into auto-approval, treat a successful login as sufficient to ensure
                // the user is assigned to the client. This backfills legacy users that pre-date assignment.
                // OnlyExternalIdp requires evidence the current session is external (idp claim set by external session manager).
                var idp = http.User.FindFirst("idp")?.Value;
                var isExternalSession = !string.IsNullOrWhiteSpace(idp);

                var canAutoAssign = client.AutoApprovalMode == AutoApprovalMode.All ||
                    (client.AutoApprovalMode == AutoApprovalMode.OnlyExternalIdp && isExternalSession);

                if (canAutoAssign)
                {
                    var userTenantId = await db.Users.AsNoTracking()
                        .Where(u => u.Id == userId)
                        .Select(u => u.TenantId)
                        .FirstOrDefaultAsync(http.RequestAborted);

                    if (userTenantId != Guid.Empty && userTenantId == client.TenantId)
                    {
                        var exists = await db.UserClientAssignments.AnyAsync(
                            a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId && a.IsActive,
                            http.RequestAborted);

                        if (!exists)
                        {
                            db.UserClientAssignments.Add(new UserClientAssignment
                            {
                                UserId = userId,
                                ClientId = client.Id,
                                RealmId = client.RealmId,
                                IsActive = true
                            });
                            await db.SaveChangesAsync(http.RequestAborted);

                            logger.LogInformation(
                                "Authorize backfilled missing client assignment. ClientId={ClientId}, ClientRecordId={ClientRecordId}, RealmId={RealmId}, UserId={UserId}, TenantId={TenantId}, AutoApprovalMode={AutoApprovalMode}, Idp={Idp}, Corr={Corr}",
                                client.ClientId, client.Id, client.RealmId, userId, userTenantId, client.AutoApprovalMode, idp ?? "(none)", corr);
                        }

                        assigned = true;
                    }
                    else
                    {
                        logger.LogInformation(
                            "Authorize auto-assign skipped due to tenant mismatch or unknown user tenant. ClientId={ClientId}, ClientRecordId={ClientRecordId}, UserId={UserId}, UserTenantId={UserTenantId}, ClientTenantId={ClientTenantId}, AutoApprovalMode={AutoApprovalMode}, Idp={Idp}, Corr={Corr}",
                            client.ClientId, client.Id, userId, userTenantId, client.TenantId, client.AutoApprovalMode, idp ?? "(none)", corr);
                    }
                }

                // Only deny if the user is STILL unassigned after the auto-assign attempt.
                if (!assigned)
                {
                    logger.LogInformation(
                        "Authorize denied: user not assigned to client. ClientId={ClientId}, ClientRecordId={ClientRecordId}, RealmId={RealmId}, UserId={UserId}, AutoApprovalMode={AutoApprovalMode}, HasExternalIdpClaim={HasIdp}, Corr={Corr}",
                        client.ClientId, client.Id, client.RealmId, userId, client.AutoApprovalMode, !string.IsNullOrWhiteSpace(idp), corr);

                    outcome = "not_assigned";
                    if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                    {
                        var uri = new UriBuilder(effectiveReq.redirect_uri);
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        query[OAuthConstants.Parameters.Error] = OAuthConstants.ErrorCodes.AccessDenied;
                        query[OAuthConstants.Parameters.ErrorDescription] = $"User is not assigned to this client (corr={corr})";
                        if (!string.IsNullOrEmpty(effectiveReq.state)) query[OAuthConstants.Parameters.State] = effectiveReq.state;
                        uri.Query = query.ToString();
                        return Results.Redirect(uri.ToString());
                    }
                    return ErrorResults.AccessDenied($"User is not assigned to this client (corr={corr})");
                }
            }

            if (validationResult.RequireConsent && !await consents.HasConsentAsync(userId, validationResult.ClientId!, validationResult.Scopes))
            {
                outcome = "consent";
                var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                var scopesQuery = string.Join("&", validationResult.Scopes.Select(s => $"Scopes={Uri.EscapeDataString(s)}"));
                var consentUrlPath = BuildTenantAwareUrl("/consent");
                var consentUrl = $"{consentUrlPath}?ClientId={Uri.EscapeDataString(validationResult.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}&{scopesQuery}";
                return Results.Redirect(consentUrl);
            }

            string? code = null;
            string? redirect = null;
            IResult? errorResult = null;

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using (var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted))
                {
                    var (ok, _, r, c) = await codes.IssueAsync(validationResult, userId);
                    if (!ok || r is null)
                    {
                        errorResult = ErrorResults.ServerError($"Failed to issue authorization code (corr={corr})");
                        return;
                    }
                    code = c;
                    redirect = r;

                    // Now that authorization succeeded, consume PAR if used
                    if (resolution.Mode == "par")
                    {
                        parStore.MarkConsumedById(resolution.ParId!);
                        metrics.ParConsumed.Add(1);
                    }
                    await transaction.CommitAsync(http.RequestAborted);
                }
            });

            if (errorResult != null) return errorResult;

            // Capture auth_time from login cookie claims
            var authTimeClaim = http.User.FindFirst(OidcConstants.Claims.AuthTime)?.Value;
            if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
            {
                meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }
            else if (!string.IsNullOrEmpty(code))
            {
                // Fallback: if auth_time not in cookie, use current time (user is authenticated NOW)
                meta.SetAuthTime(code!, DateTimeOffset.UtcNow);
            }

            // Persist RFC 8707 resource indicator with the code (if present)
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(validationResult.Resource))
            {
                meta.SetResource(code!, validationResult.Resource!);
            }

            // New: stash upstream identity context (idp/acr/amr) for propagation into tokens
            if (!string.IsNullOrEmpty(code))
            {
                var idp = http.User.FindFirst(OidcConstants.Claims.Idp)?.Value;
                var acr = http.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
                var amrValues = http.User.Claims.Where(c => c.Type == OidcConstants.Claims.Amr).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
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

                // Front-channel logout: generate sid and store with the code for ID token issuance
                var sid = http.User.FindFirst(OidcConstants.Claims.Sid)?.Value ?? Guid.NewGuid().ToString("N");
                meta.SetSid(code!, sid);
            }

            // JARM response if requested
            if (!string.IsNullOrEmpty(validationResult.ResponseMode) && (validationResult.ResponseMode == OidcConstants.ResponseModes.QueryJwt || validationResult.ResponseMode == OidcConstants.ResponseModes.FormPostJwt))
            {
                var jarmJwt = await jarm.CreateSuccessResponseAsync(validationResult.ClientId!, issuer, code!, validationResult.ResponseMode!, effectiveReq.state);
                return JarmRedirect(validationResult.RedirectUri!, validationResult.ResponseMode!, jarmJwt);
            }

            // RFC 9207: Add issuer identification parameter to prevent mix-up attacks
            var iss = http.GetIssuer();
            var uri2 = new UriBuilder(redirect!);
            var query2 = System.Web.HttpUtility.ParseQueryString(uri2.Query);
            query2["iss"] = iss;
            if (!string.IsNullOrEmpty(effectiveReq.state))
            {
                query2["state"] = effectiveReq.state;
            }
            uri2.Query = query2.ToString();
            return Results.Redirect(uri2.ToString());
        }
        finally
        {
            sw.Stop();
            var tags = new TagList { new("client", clientBucket), new("mode", mode), new("outcome", outcome) };
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }
    // BucketizeClientId moved to Bucketization utility.

    private static string BuildLastProviderCookieName(string clientId)
        => LastIdpCookiePrefix + Bucketization.BucketizeClientId(clientId);

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
        if (string.Equals(responseMode, OidcConstants.ResponseModes.QueryJwt, StringComparison.Ordinal))
        {
            var uri = new UriBuilder(redirectUri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query.Remove("code");
            query.Remove("state");
            query["response"] = jarmJwt;
            uri.Query = query.ToString();
            return Results.Redirect(uri.ToString());
        }
        if (string.Equals(responseMode, OidcConstants.ResponseModes.FormPostJwt, StringComparison.Ordinal))
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

}
