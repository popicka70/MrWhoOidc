using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Linq;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
}

public sealed class LogoutHandler(AuthDbContext db) : ILogoutHandler
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

        // Sign out local session regardless
        await http.SignOutAsync();

        // Build list of front-channel iframe URLs for all configured clients with FrontChannelLogoutUri
        var clients = await db.Clients.AsNoTracking().Where(c => c.FrontChannelLogoutUri != null && c.FrontChannelLogoutUri != "").ToListAsync();
        var iframes = new List<string>();
        foreach (var c in clients)
        {
            var uri = c.FrontChannelLogoutUri!;
            var hasQuery = uri.Contains('?', StringComparison.Ordinal);
            var sep = hasQuery ? '&' : '?';
            var url = uri + sep + "iss=" + Uri.EscapeDataString(GetIssuer(http));
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
