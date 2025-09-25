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

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
}

public sealed class LogoutHandler(AuthDbContext db, IKeyStore keyStore, ILogger<LogoutHandler> logger, OidcMetrics metrics) : ILogoutHandler
{
    public async Task<IResult> LocalLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync();
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
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
                var sidValue = !string.IsNullOrEmpty(sid) ? sid : ExtractSidFromIdToken(idTokenHint);
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
                    Sid = string.IsNullOrEmpty(sid) ? ExtractSidFromIdToken(idTokenHint) : sid,
                    Sub = TryExtractClaim(idTokenHint, "sub"),
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

        var sub = TryExtractClaim(idTokenHint, "sub");
        var sid = !string.IsNullOrEmpty(sidFromQuery) ? sidFromQuery : ExtractSidFromIdToken(idTokenHint);
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

    private static string? TryExtractClaim(string jwt, string claim)
    {
        if (string.IsNullOrEmpty(jwt) || jwt.Count(c => c == '.') != 2) return null;
        try
        {
            var parts = jwt.Split('.');
            var payload = parts[1];
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(payload.Replace('-', '+').Replace('_', '/'))));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(claim, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

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

    private static string? ExtractSidFromIdToken(string idToken)
    {
        if (string.IsNullOrEmpty(idToken) || idToken.Count(c => c == '.') != 2) return null;
        try
        {
            var parts = idToken.Split('.');
            var payload = parts[1];
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(payload.Replace('-', '+').Replace('_', '/'))));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sid", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
                return sidEl.GetString();
        }
        catch { }
        return null;
    }

    private static string Pad(string s)
    {
        return s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
    }
}
