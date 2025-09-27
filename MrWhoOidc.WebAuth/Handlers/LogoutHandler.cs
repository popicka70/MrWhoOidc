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
using System.Net;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Infrastructure;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    // Legacy local logout (kept for RP-initiated flows and existing links)
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
    Task<IResult> LogoutEntryAsync(HttpContext http);
    Task<IResult> FederatedCallbackAsync(HttpContext http);
    Task<IResult> FinalRedirectAsync(HttpContext http);
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
        var style = http.Request.Query["style"].ToString();
        // Capture potential external redirect inputs for federated flow
        var clientIdParam = http.Request.Query["client_id"].ToString();
        var postLogoutExternal = http.Request.Query["post_logout_redirect_uri"].ToString();
        if (!fedOpts.Value.Enabled)
        {
            logger.LogInformation("Federated logout disabled - performing local logout");
            audit.Emit("logout.federated.prompt.skip_disabled", new { });
            return await LocalLogoutAsync(http);
        }

        var capability = await upstreamLogoutSvc.CanFederateAsync(http.User, http.RequestAborted);
        if (!capability.CanFederate)
        {
            audit.Emit("logout.federated.prompt.skip_no_capability", new { });
            return await LocalLogoutAsync(http);
        }

        var idpDisplay = capability.ProviderDisplayName ?? capability.ProviderName;
        var formReturn = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        audit.Emit("logout.federated.prompt", new { provider = capability.ProviderName });
        metrics.LogoutRequests.Add(1, new KeyValuePair<string, object?>("mode", "prompt"));
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "prompt"));
        var qStyle = string.IsNullOrEmpty(style) ? string.Empty : $"&style={System.Web.HttpUtility.UrlEncode(style)}";
        var qClient = string.IsNullOrEmpty(clientIdParam) ? string.Empty : $"&client_id={System.Web.HttpUtility.UrlEncode(clientIdParam)}";
        var qPlru = string.IsNullOrEmpty(postLogoutExternal) ? string.Empty : $"&post_logout_redirect_uri={System.Web.HttpUtility.UrlEncode(postLogoutExternal)}";
        return Results.Redirect($"/Logout/Prompt?provider={System.Web.HttpUtility.UrlEncode(idpDisplay)}&ret={System.Web.HttpUtility.UrlEncode(formReturn)}{qStyle}{qClient}{qPlru}");
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
            audit.Emit("logout.federated.callback.page.fail", new { reason = validation.Reason });
            // Show Razor error page with reason parameter
            var reasonParam = validation.Reason ?? "invalid_state";
            return Results.Redirect($"/Logout/FederatedCallbackError?reason={WebUtility.UrlEncode(reasonParam)}");
        }
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_callback"));
        audit.Emit("logout.federated.callback.page.ok", new { has_ref = validation.RefId != null });
        if (!string.IsNullOrWhiteSpace(validation.RefId))
        {
            return Results.Redirect($"/logout/final?ref={Uri.EscapeDataString(validation.RefId!)}");
        }
        if (!string.IsNullOrWhiteSpace(validation.ReturnUrl))
        {
            return Results.Redirect(validation.ReturnUrl);
        }
        // Show standard Razor page (layout includes style scheme)
        return Results.Redirect("/Logout/FederatedSignedOut");
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
                if (!string.IsNullOrEmpty(sidValue)) url += "&sid=" + Uri.EscapeDataString(sidValue);
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
        string? refId = null; // opaque reference id to be used on final redirect endpoint
        if (!string.IsNullOrEmpty(postLogout) && !string.IsNullOrEmpty(clientId))
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client is not null && !string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson))
            {
                try
                {
                    var allowed = JsonSerializer.Deserialize<string[]>(client.AllowedLogoutRedirectUrisJson!) ?? Array.Empty<string>();
                    if (allowed.Contains(postLogout, StringComparer.Ordinal))
                    {
                        // Create opaque reference (stateful) stored server-side instead of exposing external URL directly
                        var idBytes = RandomNumberGenerator.GetBytes(16); // 128-bit
                        var id = Base64UrlEncoder.Encode(idBytes); // url-safe, no padding
                        var entity = new LogoutRedirectReference
                        {
                            Id = id,
                            ClientId = clientId,
                            RedirectUri = postLogout,
                            State = string.IsNullOrEmpty(state) ? null : state,
                            CreatedAt = DateTimeOffset.UtcNow,
                            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                            Used = false
                        };
                        db.LogoutRedirectReferences.Add(entity);
                        await db.SaveChangesAsync();
                        refId = id;
                        audit.Emit("logout.redirect.ref.created", new { client_id = clientId, has_state = !string.IsNullOrEmpty(state) });
                    }
                }
                catch { /* ignore parse failure => treat as not allowed */ }
            }
        }

        // Render HTML page with hidden iframes that trigger front-channel logout on RPs, then redirect via opaque ref if available
        var html = BuildFrontChannelPage(iframes, refId, state);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // New final redirect resolution endpoint
    public async Task<IResult> FinalRedirectAsync(HttpContext http)
    {
        var refId = http.Request.Query["ref"].ToString();
        if (string.IsNullOrWhiteSpace(refId)) return Results.BadRequest();
        var record = await db.LogoutRedirectReferences.FirstOrDefaultAsync(r => r.Id == refId);
        if (record is null) return Results.BadRequest();
        if (record.ExpiresAt < DateTimeOffset.UtcNow || record.Used)
        {
            return Results.BadRequest();
        }
        record.Used = true;
        await db.SaveChangesAsync();
        // Append state if present and not already present
        var dest = record.RedirectUri;
        if (!string.IsNullOrEmpty(record.State))
        {
            var ub = new UriBuilder(dest);
            var q = System.Web.HttpUtility.ParseQueryString(ub.Query);
            if (string.IsNullOrEmpty(q["state"])) q["state"] = record.State;
            ub.Query = q.ToString();
            dest = ub.ToString();
        }
        audit.Emit("logout.redirect.ref.used", new { client_id = record.ClientId });
        return Results.Redirect(dest);
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
    private static string BuildFrontChannelPage(IEnumerable<string> iframes, string? refId, string? state)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Logout</title><meta http-equiv=\"cache-control\" content=\"no-cache\"/></head><body>");
        foreach (var src in iframes)
        {
            sb.Append("<iframe src=\"");
            sb.Append(System.Web.HttpUtility.HtmlAttributeEncode(src));
            sb.Append("\" style=\"display:none;width:0;height:0;border:0\"></iframe>");
        }
        if (!string.IsNullOrEmpty(refId))
        {
            var finalUrl = "/logout/final?ref=" + System.Web.HttpUtility.UrlEncode(refId);
            sb.Append("<script>setTimeout(function(){ window.location.replace('");
            sb.Append(System.Web.HttpUtility.JavaScriptStringEncode(finalUrl));
            sb.Append("'); }, 200);</script>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GetIssuer(HttpContext http)
        => (http.RequestServices.GetService(typeof(OidcOptions)) as OidcOptions)?.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

    // Sid extraction now uses JwtLightParser.
}
