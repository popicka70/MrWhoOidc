using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IExternalOidcHandler
{
    Task<IResult> StartAsync(HttpContext http);
    Task<IResult> CallbackAsync(HttpContext http);
}

public sealed class ExternalOidcHandler(AuthDbContext db, IHttpClientFactory httpFactory, IDataProtectionProvider dp) : IExternalOidcHandler
{
    private readonly IDataProtector _protector = dp.CreateProtector("ext-oidc-state");

    public async Task<IResult> StartAsync(HttpContext http)
    {
        var providerName = http.Request.Query["provider"].ToString();
        var returnUrl = http.Request.Query["returnUrl"].ToString(); // original /authorize URL
        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(returnUrl))
            return Results.BadRequest("provider and returnUrl are required");

        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == providerName && p.Enabled);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson))
            return Results.BadRequest("Unknown provider");
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null)
            return Results.BadRequest("Invalid provider configuration");

        // Discovery
        var httpc = httpFactory.CreateClient();
        var discoUrl = string.IsNullOrWhiteSpace(cfg.DiscoveryUrl) ? cfg.Authority.TrimEnd('/') + "/.well-known/openid-configuration" : cfg.DiscoveryUrl!;
        using var resp = await httpc.GetAsync(discoUrl, http.RequestAborted);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(http.RequestAborted));
        var root = doc.RootElement;
        var authz = root.GetProperty("authorization_endpoint").GetString()!;

        // PKCE
        var verifier = Base64Url(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")));
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

        var cb = $"{http.Request.Scheme}://{http.Request.Host}/Auth/External/Callback";

        // State
        var statePayload = JsonSerializer.Serialize(new StateModel { Provider = providerName, CodeVerifier = verifier, ReturnUrl = returnUrl });
        var state = Base64Url(_protector.Protect(Encoding.UTF8.GetBytes(statePayload)));

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = cfg.ResponseType ?? "code",
            ["client_id"] = cfg.ClientId,
            ["redirect_uri"] = cb,
            ["scope"] = string.Join(' ', cfg.Scopes ?? new[] { "openid", "profile", "email" }),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        // Copy hints from returnUrl
        try
        {
            var ru = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            var qs = System.Web.HttpUtility.ParseQueryString(ru.IsAbsoluteUri ? ru.Query : new Uri("http://local" + returnUrl).Query);
            void TryCopy(string name)
            {
                var val = qs[name];
                if (!string.IsNullOrEmpty(val)) query[name] = val;
            }
            TryCopy("login_hint");
            TryCopy("ui_locales");
            TryCopy("prompt");
            TryCopy("max_age");
            TryCopy("acr_values");
        }
        catch { }

        var ub = new UriBuilder(authz);
        var q = System.Web.HttpUtility.ParseQueryString(ub.Query);
        foreach (var kv in query) if (!string.IsNullOrEmpty(kv.Value)) q[kv.Key] = kv.Value;
        ub.Query = q.ToString();
        return Results.Redirect(ub.ToString());
    }

    public async Task<IResult> CallbackAsync(HttpContext http)
    {
        var code = http.Request.Query["code"].ToString();
        var stateRaw = http.Request.Query["state"].ToString();
        var error = http.Request.Query["error"].ToString();
        if (!string.IsNullOrEmpty(error)) return Results.Content($"Upstream error: {error}", "text/plain", statusCode: 400);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(stateRaw)) return Results.BadRequest("Missing code/state");

        StateModel state;
        try
        {
            var bytes = _protector.Unprotect(Base64UrlDecode(stateRaw));
            state = JsonSerializer.Deserialize<StateModel>(bytes)!;
        }
        catch { return Results.BadRequest("Invalid state"); }

        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == state.Provider && p.Enabled);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson)) return Results.BadRequest("Unknown provider");
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null) return Results.BadRequest("Invalid provider configuration");

        // Discovery
        var httpc = httpFactory.CreateClient();
        var discoUrl = string.IsNullOrWhiteSpace(cfg.DiscoveryUrl) ? cfg.Authority.TrimEnd('/') + "/.well-known/openid-configuration" : cfg.DiscoveryUrl!;
        using var resp = await httpc.GetAsync(discoUrl, http.RequestAborted);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(http.RequestAborted));
        var root = doc.RootElement;
        var tokenEndpoint = root.GetProperty("token_endpoint").GetString()!;
        var userinfoEndpoint = root.TryGetProperty("userinfo_endpoint", out var ue) ? ue.GetString() : null;

        var form = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = $"{http.Request.Scheme}://{http.Request.Host}/Auth/External/Callback",
            ["client_id"] = cfg.ClientId,
            ["code_verifier"] = state.CodeVerifier
        };
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;

        using var msg = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var tokResp = await httpc.SendAsync(msg, http.RequestAborted);
        var body = await tokResp.Content.ReadAsStringAsync(http.RequestAborted);
        if (!tokResp.IsSuccessStatusCode) return Results.Content($"Token exchange failed: {(int)tokResp.StatusCode} {body}", "text/plain", statusCode: 400);

        using var tokDoc = JsonDocument.Parse(body);
        var idToken = tokDoc.RootElement.TryGetProperty("id_token", out var idt) ? idt.GetString() : null;
        string? email = null, name = null, sub = null;
        if (!string.IsNullOrEmpty(idToken))
        {
            var parts = idToken.Split('.');
            if (parts.Length >= 2)
            {
                var payload = JsonDocument.Parse(Base64UrlDecodeToString(parts[1]));
                sub = payload.RootElement.TryGetProperty("sub", out var s) ? s.GetString() : null;
                email = payload.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
                name = payload.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            }
        }
        if (userinfoEndpoint is not null && string.IsNullOrEmpty(email))
        {
            try
            {
                var at = tokDoc.RootElement.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
                if (!string.IsNullOrEmpty(at))
                {
                    var uiReq = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
                    uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", at);
                    using var uiResp = await httpc.SendAsync(uiReq, http.RequestAborted);
                    var uiBody = await uiResp.Content.ReadAsStringAsync(http.RequestAborted);
                    if (uiResp.IsSuccessStatusCode)
                    {
                        using var uiDoc = JsonDocument.Parse(uiBody);
                        sub ??= uiDoc.RootElement.TryGetProperty("sub", out var s) ? s.GetString() : null;
                        email ??= uiDoc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
                        name ??= uiDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                    }
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(sub)) return Results.Content("Missing subject from upstream IdP", "text/plain", statusCode: 400);

        var username = !string.IsNullOrEmpty(email) ? email : $"{state.Provider}:{sub}";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username || (!string.IsNullOrEmpty(email) && u.Email == email));
        if (user is null)
        {
            user = new User { Username = username, Email = email, Name = name ?? username, PasswordHash = string.Empty, HashAlgorithm = "external" };
            db.Users.Add(user);
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(System.Security.Claims.ClaimTypes.Name, user.Name ?? user.Username),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        var id = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new System.Security.Claims.ClaimsPrincipal(id);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        var redirect = state.ReturnUrl ?? "/";
        return Results.Redirect(redirect);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlDecodeToString(string s)
        => Encoding.UTF8.GetString(Base64UrlDecode(s));

    private sealed class StateModel
    {
        public string Provider { get; set; } = string.Empty;
        public string CodeVerifier { get; set; } = string.Empty;
        public string? ReturnUrl { get; set; }
    }
}
