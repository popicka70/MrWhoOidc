using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Infrastructure;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    // Legacy local logout (kept for RP-initiated flows and existing links)
    Task<IResult> LocalLogoutAsync(HttpContext http);
    // OIDC end session endpoint (RP-initiated logout with front/back-channel to clients)
    Task<IResult> EndSessionAsync(HttpContext http);
    // New entry point that can present federated option
    Task<IResult> LogoutEntryAsync(HttpContext http);
    Task<IResult> LogoutPostAsync(HttpContext http);
    Task<IResult> FederatedCallbackAsync(HttpContext http);
}

public sealed class LogoutHandler(AuthDbContext db,
    IKeyStore keyStore,
    ILogger<LogoutHandler> logger,
    OidcMetrics metrics,
    MrWhoOidc.WebAuth.Observability.IAuditSink audit,
    IUpstreamLogoutService upstreamLogoutSvc,
    IOptions<FederatedLogoutOptions> fedOpts) : ILogoutHandler
{
    public async Task<IResult> LocalLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync();
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }

    public async Task<IResult> LogoutEntryAsync(HttpContext http)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        if (!fedOpts.Value.Enabled)
        {
            logger.LogInformation("Federated logout disabled - performing local logout");
            audit.Emit("logout.federated.prompt.skip_disabled", new { });
            return await LocalLogoutAsync(http);
        }

        var capability = await upstreamLogoutSvc.CanFederateAsync(http.User, http.RequestAborted);
        if (!capability.CanFederate)
        {
            // Fall back to legacy local logout
            audit.Emit("logout.federated.prompt.skip_no_capability", new { });
            return await LocalLogoutAsync(http);
        }

        // Render simple choice HTML (minimal styling; Razor page could replace later)
        var idpDisplay = System.Web.HttpUtility.HtmlEncode(capability.ProviderDisplayName ?? capability.ProviderName);
        var formReturn = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        var html = $"<!DOCTYPE html><html><head><title>Sign out</title><meta charset='utf-8'/><meta http-equiv='cache-control' content='no-cache' /></head><body>\n<h2>Sign out</h2>\n<p>You signed in using external provider <strong>{idpDisplay}</strong>. Choose how you want to sign out:</p>\n<form method='post' action='/logout'>\n  <input type='hidden' name='returnUrl' value='{System.Web.HttpUtility.HtmlAttributeEncode(formReturn)}' />\n  <div><label><input type='radio' name='mode' value='local' checked /> Sign out only from this application</label></div>\n  <div><label><input type='radio' name='mode' value='federated' /> Sign out here and at {idpDisplay}</label></div>\n  <p style='font-size:smaller;color:#555'>Local-only leaves you signed in at the external provider; other apps using it may remain signed in.</p>\n  <button type='submit'>Continue</button>\n</form>\n</body></html>";
        audit.Emit("logout.federated.prompt", new { provider = capability.ProviderName });
        metrics.LogoutRequests.Add(1, new KeyValuePair<string, object?>("mode", "prompt"));
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "prompt"));
        return Results.Content(html, "text/html; charset=utf-8");
    }

    public async Task<IResult> LogoutPostAsync(HttpContext http)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mode = http.Request.Form["mode"].ToString();
        var returnUrl = http.Request.Form["returnUrl"].ToString();
        if (string.IsNullOrEmpty(mode)) mode = "local"; // safe default
        if (mode == "local")
        {
            metrics.LogoutLocal.Add(1);
            audit.Emit("logout.federated.choice.local", new { return_hash = audit.HashValue(returnUrl) });
            var res = await LocalLogoutAsync(http);
            metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "local"));
            return res;
        }
        if (mode == "federated")
        {
            audit.Emit("logout.federated.choice.federated", new { return_hash = audit.HashValue(returnUrl) });
            var capability = await upstreamLogoutSvc.CanFederateAsync(http.User, http.RequestAborted);
            if (!capability.CanFederate)
            {
                logger.LogWarning("Federated logout chosen but capability missing - falling back to local");
                metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "capability_missing"));
                audit.Emit("logout.federated.choice.federated.capability_missing", new { });
                var resFallback = await LocalLogoutAsync(http);
                metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "fallback_local"));
                return resFallback;
            }

            // Extract upstream metadata from auth properties if still authenticated (should be prior to SignOut)
            string? encIdToken = null; string? upstreamSid = null;
            if (http.User?.Identity?.IsAuthenticated == true)
            {
                // Retrieve current auth ticket to access AuthenticationProperties (framework lacks direct API; try AuthenticateAsync)
                var authResult = await http.AuthenticateAsync();
                encIdToken = authResult?.Properties?.Items?.TryGetValue("UpstreamIdTokenEnc", out var enc) == true ? enc : null;
                upstreamSid = authResult?.Properties?.Items?.TryGetValue("UpstreamSid", out var sidVal) == true ? sidVal : null;
            }
            var callbackBase = $"{http.Request.Scheme}://{http.Request.Host}";
            var principal = http.User ?? new ClaimsPrincipal();
            var redirectModel = await upstreamLogoutSvc.BuildFederatedRedirectAsync(principal, encIdToken, upstreamSid, callbackBase, returnUrl, http.RequestAborted);
            if (!redirectModel.Success)
            {
                logger.LogWarning("Failed to build federated logout redirect: {Reason}", redirectModel.FailureReason);
                metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", redirectModel.FailureReason));
                audit.Emit("logout.federated.redirect.fail", new { reason = redirectModel.FailureReason });
                var resLocal = await LocalLogoutAsync(http);
                metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "fallback_local"));
                return resLocal;
            }

            await http.SignOutAsync(); // Ensure local cookie cleared first
            metrics.LogoutFederated.Add(1);
            metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_redirect"));
            return Results.Redirect(redirectModel.RedirectUrl ?? "/");
        }
        // Unknown mode => local
        audit.Emit("logout.federated.choice.unknown", new { mode });
        var resDefault = await LocalLogoutAsync(http);
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "unknown_local"));
        return resDefault;
    }

    public async Task<IResult> FederatedCallbackAsync(HttpContext http)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = http.Request.Query["state"].ToString();
        var validation = await upstreamLogoutSvc.ValidateCallbackAsync(state, http.RequestAborted);
        if (!validation.Valid)
        {
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", validation.Reason ?? "invalid_state"));
            metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_callback_fail"));
            var htmlBad = "<!DOCTYPE html><html><body><h2>Logout complete (local)</h2><p>The external logout response could not be validated. You are signed out locally.</p></body></html>";
            audit.Emit("logout.federated.callback.page.fail", new { reason = validation.Reason });
            return Results.Content(htmlBad, "text/html; charset=utf-8");
        }
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_callback"));
        audit.Emit("logout.federated.callback.page.ok", new { });
        var html = "<!DOCTYPE html><html><body><h2>Signed out</h2><p>You have been signed out from this application and the external provider.</p></body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    public async Task<IResult> EndSessionAsync(HttpContext http)
    {
        // OIDC RP-initiated logout with optional front-channel notifications
        var idTokenHint = http.Request.Query["id_token_hint"].ToString();
        var postLogout = http.Request.Query["post_logout_redirect_uri"].ToString();
        var state = http.Request.Query["state"].ToString();
        var sid = http.Request.Query["sid"].ToString();
        var issuer = GetIssuer(http);

        // Sign out local session regardless
        await http.SignOutAsync();

        // Build list of front-channel iframe URLs for all configured clients with FrontChannelLogoutUri
        var clients = await db.Clients.AsNoTracking().ToListAsync();
        var frontChannelClients = clients.Where(c => !string.IsNullOrEmpty(c.FrontChannelLogoutUri)).ToList();
        var iframes = new List<string>();
        foreach (var c in frontChannelClients)
        {
            var uri = c.FrontChannelLogoutUri!;
            var hasQuery = uri.Contains('?', StringComparison.Ordinal);
            var sep = hasQuery ? '&' : '?';
            var url = uri + sep + "iss=" + Uri.EscapeDataString(issuer);
            if (c.FrontChannelLogoutSessionRequired)
            {
                var sidValue = !string.IsNullOrEmpty(sid) ? sid : JwtLightParser.TryGetClaim(idTokenHint, "sid");
                if (!string.IsNullOrEmpty(sidValue))
                {
                    url += "&sid=" + Uri.EscapeDataString(sidValue);
                }
            }
            iframes.Add(url);
        }

        // Back-channel logout: POST logout_token JWT to registered backchannel URLs
        var backChannelClients = clients.Where(c => !string.IsNullOrEmpty(c.BackChannelLogoutUri)).ToList();
        if (backChannelClients.Count > 0)
        {
            // Feature flag: Backchannel.Enabled (default true)
            var feature = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MrWhoOidc.WebAuth.Background.BackchannelFeatureOptions>>();
            if (!feature.CurrentValue.Enabled)
            {
                logger.LogInformation("BCL emission disabled by feature flag - skipping enqueue for {Count} clients", backChannelClients.Count);
            }
            else
            {
            // Enqueue into outbox for background delivery
            foreach (var c in backChannelClients)
            {
                var token = CreateLogoutToken(issuer, c.ClientId, idTokenHint, sid);
                if (token is null) continue;

                // Allow/block list by host (optional via config)
                if (Uri.TryCreate(c.BackChannelLogoutUri, UriKind.Absolute, out var target))
                {
                    var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
                    var allowList = cfg.GetSection("Backchannel:AllowHosts").Get<string[]>() ?? Array.Empty<string>();
                    var blockList = cfg.GetSection("Backchannel:BlockHosts").Get<string[]>() ?? Array.Empty<string>();
                    var host = target.Host;
                    if (blockList.Contains(host, StringComparer.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("Skipping BCL for client {ClientId}: host {Host} is blocked", c.ClientId, host);
                        continue;
                    }
                    if (allowList.Length > 0 && !allowList.Contains(host, StringComparer.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("Skipping BCL for client {ClientId}: host {Host} not in allow-list", c.ClientId, host);
                        continue;
                    }
                }

                var entity = new BackchannelLogoutNotification
                {
                    ClientDbId = c.Id,
                    ClientId = c.ClientId,
                    TargetUri = c.BackChannelLogoutUri!,
                    LogoutToken = token,
                    Sid = string.IsNullOrEmpty(sid) ? JwtLightParser.TryGetClaim(idTokenHint, "sid") : sid,
                    Sub = JwtLightParser.TryGetClaim(idTokenHint, "sub"),
                    Status = "pending",
                    AttemptCount = 0,
                    MaxAttempts = 5,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.BackchannelLogoutNotifications.Add(entity);
                // Audit-like info in logs for enqueue (PII minimized): who/what/when
                logger.LogInformation("BCL enqueue: client={ClientId} target={TargetHost} sid={HasSid} sub={HasSub}",
                    entity.ClientId, new Uri(entity.TargetUri).Host, !string.IsNullOrEmpty(entity.Sid), !string.IsNullOrEmpty(entity.Sub));
                metrics.BclEmitted.Add(1, new KeyValuePair<string, object?>("client_id", entity.ClientId));
                // Audit event (no raw tokens)
                var httpIp = http.Connection.RemoteIpAddress?.ToString();
                audit.Emit("bcl.enqueue", new
                {
                    client_id = entity.ClientId,
                    target = new Uri(entity.TargetUri).Host,
                    sid_hash = audit.HashValue(entity.Sid),
                    sub_hash = audit.HashValue(entity.Sub),
                    created_at = entity.CreatedAt,
                    ip = httpIp
                });
            }
            await db.SaveChangesAsync();
            }
        }

        // Validate post_logout_redirect_uri against allow-list if a client parameter is present
        var clientId = http.Request.Query["client_id"].ToString();
        if (!string.IsNullOrEmpty(postLogout) && !string.IsNullOrEmpty(clientId))
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client is not null && !string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson))
            {
                try
                {
                    var allowed = JsonSerializer.Deserialize<string[]>(client.AllowedLogoutRedirectUrisJson!) ?? Array.Empty<string>();
                    if (!allowed.Contains(postLogout, StringComparer.Ordinal))
                    {
                        postLogout = null; // not allowed
                    }
                }
                catch { postLogout = null; }
            }
        }

        // Render HTML page with hidden iframes that trigger front-channel logout on RPs, then redirect if requested
        var html = BuildFrontChannelPage(iframes, postLogout, state);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private string? CreateLogoutToken(string issuer, string audienceClientId, string idTokenHint, string sidFromQuery)
    {
        // Build payload per spec with JSON events claim
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new JwtPayload
        {
            { "iss", issuer },
            { "aud", audienceClientId },
            { "iat", now },
            { "jti", Guid.NewGuid().ToString("N") },
            { "events", new Dictionary<string, object> { { "http://schemas.openid.net/event/backchannel-logout", new Dictionary<string, object>() } } }
        };

    var sub = JwtLightParser.TryGetClaim(idTokenHint, "sub");
    var sid = !string.IsNullOrEmpty(sidFromQuery) ? sidFromQuery : JwtLightParser.TryGetClaim(idTokenHint, "sid");
        if (!string.IsNullOrEmpty(sub)) payload["sub"] = sub;
        if (!string.IsNullOrEmpty(sid)) payload["sid"] = sid;
        if (string.IsNullOrEmpty(sub) && string.IsNullOrEmpty(sid))
        {
            // Spec requires at least one of sid or sub
            return null;
        }

        var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
        var jsonWebKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
        var creds = new SigningCredentials(jsonWebKey, SecurityAlgorithms.RsaSha256);
        var header = new JwtHeader(creds)
        {
            { "typ", "logout+jwt" }
        };

        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(header, payload);
        token.Payload["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        return handler.WriteToken(token);
    }

    // Claim extraction moved to JwtLightParser.
    private static string BuildFrontChannelPage(IEnumerable<string> iframes, string? redirect, string? state)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Logout</title><meta http-equiv=\"cache-control\" content=\"no-cache\"/></head><body>");
        foreach (var src in iframes)
        {
            sb.Append("<iframe src=\"");
            sb.Append(System.Web.HttpUtility.HtmlAttributeEncode(src));
            sb.Append("\" style=\"display:none;width:0;height:0;border:0\"></iframe>");
        }
        if (!string.IsNullOrEmpty(redirect))
        {
            // Preserve state if provided
            var r = redirect;
            if (!string.IsNullOrEmpty(state))
            {
                var ub = new UriBuilder(redirect);
                var q = System.Web.HttpUtility.ParseQueryString(ub.Query);
                q["state"] = state;
                ub.Query = q.ToString();
                r = ub.ToString();
            }
            sb.Append("<script>setTimeout(function(){ window.location.replace('");
            sb.Append(System.Web.HttpUtility.JavaScriptStringEncode(r));
            sb.Append("'); }, 200);</script>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GetIssuer(HttpContext http)
        => (http.RequestServices.GetService(typeof(OidcOptions)) as OidcOptions)?.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

    // Sid extraction now uses JwtLightParser.
}
