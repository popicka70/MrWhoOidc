using MrWhoOidc.Web;
using MrWhoOidc.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Logging;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using MrWhoOidc.Web.DPoP;
using MrWhoOidc.Web.JAR;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Reduce diagnostic logging and disable PII
builder.Logging.AddFilter("Microsoft.IdentityModel", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Information);
IdentityModelEventSource.ShowPII = false;

// Read Authority from configuration (required). Supports both 'Oidc' and 'OIDC'.
string? authorityRaw = builder.Configuration["Oidc:Authority"] ?? builder.Configuration["OIDC:Authority"];
string responseMode = (builder.Configuration["Oidc:ResponseMode"] ?? "query.jwt").ToLowerInvariant(); // query.jwt | form_post.jwt

// Add services to the container.
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// DPoP key store for OIDC backchannel
builder.Services.AddSingleton<DPoPKeyStore>();

// Register a typed DPoP-enabled backchannel HttpClient via DI to avoid BuildServiceProvider
builder.Services.AddHttpClient("OidcBackchannel")
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true // dev only
            }
        };
        return new DPoPBackchannelHandler(sp.GetRequiredService<DPoPKeyStore>(), sockets);
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

// Configure OIDC Backchannel via options and IHttpClientFactory to avoid BuildServiceProvider
builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
    .Configure<IHttpClientFactory>((options, factory) =>
    {
        options.Backchannel = factory.CreateClient("OidcBackchannel");
    });

// JAR/PAR service
builder.Services.AddSingleton<JarParService>();

// AuthN/Z
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = ".mrwhooidc.web";
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Validate principal against backchannel logout blacklist
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var sid = ctx.Principal?.FindFirst("sid")?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                var store = ctx.HttpContext.RequestServices.GetRequiredService<BackchannelLogoutStore>();
                if (store.IsSidRevoked(sid))
                {
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    ctx.RejectPrincipal();
                }
            }
        };
    })
    .AddOpenIdConnect(options =>
    {
        if (string.IsNullOrWhiteSpace(authorityRaw))
            throw new InvalidOperationException("Oidc:Authority (or OIDC:Authority) must be configured to use OpenID Connect.");

        if (!Uri.TryCreate(authorityRaw, UriKind.Absolute, out var authorityUri))
            throw new InvalidOperationException($"Invalid OIDC Authority URI: '{authorityRaw}'.");

        var normalizedAuthority = authorityUri.GetLeftPart(UriPartial.Authority) + authorityUri.AbsolutePath.TrimEnd('/') + "/";

        options.Authority = normalizedAuthority;
        options.RequireHttpsMetadata = normalizedAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        options.ClientId = builder.Configuration["Oidc:ClientId"] ?? builder.Configuration["OIDC:ClientId"] ?? "blazor-web";
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? builder.Configuration["OIDC:ClientSecret"]; // optional for public client
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");

        // Ensure Identity.Name reads from the 'name' claim in ID token/userinfo
        options.TokenValidationParameters.NameClaimType = "name";

        // If using http:// for dev metadata, configure ConfigurationManager with RequireHttps=false
        if (normalizedAuthority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var metadataUri = new Uri(new Uri(normalizedAuthority), ".well-known/openid-configuration");
            options.MetadataAddress = metadataUri.ToString();
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                options.MetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = false }
            );
        }

        options.TokenValidationParameters.ValidateIssuer = false; // dev only

        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = async ctx =>
            {
                try
                {
                    var config = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                    var privateJwk = config["Oidc:PrivateJwk"] ?? config["OIDC:PrivateJwk"];
                    if (!string.IsNullOrWhiteSpace(privateJwk))
                    {
                        var jar = ctx.HttpContext.RequestServices.GetRequiredService<JarParService>();
                        var authority = ctx.Options.Authority!;

                        // Use values generated by the OIDC handler (ensures PKCE/state cookies match)
                        var clientId = ctx.Options.ClientId!;
                        var redirectUri = ctx.ProtocolMessage.RedirectUri;
                        var scope = ctx.ProtocolMessage.Scope;
                        var state = ctx.ProtocolMessage.State;
                        var nonce = ctx.ProtocolMessage.Nonce;
                        ctx.ProtocolMessage.Parameters.TryGetValue("code_challenge", out var codeChal);
                        ctx.ProtocolMessage.Parameters.TryGetValue("code_challenge_method", out var codeChalMethod);
                        var resource = ctx.ProtocolMessage.Resource;

                        var requestUri = await jar.CreateParAsync(authority, clientId, redirectUri, scope!, state!, nonce!, codeChal!, codeChalMethod ?? "S256", resource);

                        // JARM: record the response mode so server knows to return JWT response
                        ctx.ProtocolMessage.Parameters["response_mode"] = responseMode;

                        // Add request_uri; keep other params so state/correlation remains intact (server sanitizes URL)
                        ctx.ProtocolMessage.Parameters["request_uri"] = requestUri;
                    }
                }
                catch (Exception ex)
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("OIDC").LogError(ex, "Failed to build PAR request");
                    throw;
                }
            },
            OnAuthenticationFailed = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC").LogError(ctx.Exception, "OIDC authentication failed");
                return Task.CompletedTask;
            },
            OnRemoteFailure = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OIDC").LogError(ctx.Failure, "OIDC remote failure");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();

// Backchannel logout store
builder.Services.AddSingleton<BackchannelLogoutStore>();

builder.Services.AddHttpClient<WeatherApiClient>(client =>
{
    client.BaseAddress = new("https+http://apiservice");
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseOutputCache();
app.UseAuthentication();
app.UseAuthorization();

// Login/Logout helpers
app.MapGet("/login", async ctx =>
{
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = ctx.Request.Query["returnUrl"].FirstOrDefault() ?? "/"
    });
}).ExcludeFromDescription();

app.MapGet("/logout", async ctx =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    // Accept both redirectUri and returnUrl; default to '/'
    var requested = ctx.Request.Query["redirectUri"].FirstOrDefault()
                   ?? ctx.Request.Query["returnUrl"].FirstOrDefault()
                   ?? "/";

    // Build absolute URI for post_logout_redirect_uri
    string absoluteRedirectUri;
    if (Uri.IsWellFormedUriString(requested, UriKind.Absolute))
    {
        absoluteRedirectUri = requested;
    }
    else
    {
        var path = requested.StartsWith('/') ? requested : "/" + requested;
        absoluteRedirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}{path}";
    }

    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = absoluteRedirectUri
    });
}).ExcludeFromDescription();

// Backchannel logout receiver (per OIDC): accepts logout_token and invalidates sessions by sid
app.MapPost("/backchannel-logout", async ctx =>
{
    // Read form-encoded body
    if (!ctx.Request.HasFormContentType)
    {
        ctx.Response.StatusCode = 400; return;
    }
    var form = await ctx.Request.ReadFormAsync();
    var logoutToken = form["logout_token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(logoutToken)) { ctx.Response.StatusCode = 400; return; }

    // Parse JWT minimally to extract sid claim; validation is optional here if using same OP
    string? sid = null;
    try
    {
        var parts = logoutToken.Split('.');
        if (parts.Length == 3)
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sid", out var sidEl) && sidEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                sid = sidEl.GetString();
            }
        }
    }
    catch { }

    if (!string.IsNullOrEmpty(sid))
    {
        var store = ctx.RequestServices.GetRequiredService<BackchannelLogoutStore>();
        store.RevokeSid(sid);
    }
    ctx.Response.StatusCode = 200;
}).ExcludeFromDescription();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();

public sealed class BackchannelLogoutStore
{
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public void RevokeSid(string sid)
    {
        lock (_gate) _revoked.Add(sid);
    }
    public bool IsSidRevoked(string sid)
    {
        lock (_gate) return _revoked.Contains(sid);
    }
}
