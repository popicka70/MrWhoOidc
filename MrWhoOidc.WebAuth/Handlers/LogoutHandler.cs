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

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
}

public sealed class LogoutHandler(AuthDbContext db, IKeyStore keyStore, IHttpClientFactory httpFactory, ILogger<LogoutHandler> logger) : ILogoutHandler
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
            // Create HttpClient once
            var httpClient = httpFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            foreach (var c in backChannelClients)
            {
                try
                {
                    var logoutToken = CreateLogoutToken(issuer, c.ClientId, idTokenHint, sid);
                    using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("logout_token", logoutToken) });
                    // Per spec, content-type is application/x-www-form-urlencoded
                    var resp = await httpClient.PostAsync(c.BackChannelLogoutUri, content);
                    if (!resp.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Backchannel logout failed for client {ClientId}: {Status}", c.ClientId, (int)resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Backchannel logout exception for client {ClientId}", c.ClientId);
                }
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

    private string CreateLogoutToken(string issuer, string audienceClientId, string idTokenHint, string sidFromQuery)
    {
        var claims = new List<Claim>
        {
            new("iss", issuer),
            new("aud", audienceClientId),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("jti", Guid.NewGuid().ToString("N")),
            new("events", "{\"http://schemas.openid.net/event/backchannel-logout\":{}}")
        };
        // Include sub if id_token_hint present and parseable
        var sub = TryExtractClaim(idTokenHint, "sub");
        if (!string.IsNullOrEmpty(sub)) claims.Add(new("sub", sub));
        // Include sid if provided or can be extracted
        var sid = !string.IsNullOrEmpty(sidFromQuery) ? sidFromQuery : ExtractSidFromIdToken(idTokenHint);
        if (!string.IsNullOrEmpty(sid)) claims.Add(new("sid", sid));

        var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
        var jsonWebKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
        var creds = new SigningCredentials(jsonWebKey, SecurityAlgorithms.RsaSha256);
        var handler = new JwtSecurityTokenHandler();
        var token = new JwtSecurityToken(issuer, audienceClientId, claims, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), creds);
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
