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
using MrWhoOidc.Web.Backchannel;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;

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
builder.Services.AddOptions<BackchannelOptions>();
builder.Services.Configure<BackchannelOptions>(builder.Configuration.GetSection("Backchannel"));

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
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    builder.Environment.IsDevelopment() // Only bypass cert validation in development
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
                var store = ctx.HttpContext.RequestServices.GetRequiredService<IRevocationStore>();
                if (await store.IsSidRevokedAsync(sid))
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
        options.ResponseMode = "form_post.jwt"; // JARM: JWT-secured authorization response
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");

        // Disable framework-level PAR (OP doesn't support it; use custom PAR via OnRedirectToIdentityProvider if needed)
        options.PushedAuthorizationBehavior = Microsoft.AspNetCore.Authentication.OpenIdConnect.PushedAuthorizationBehavior.Disable;

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
            OnMessageReceived = ctx =>
            {
                // JARM: Decode JWT authorization response (form_post.jwt or query.jwt)
                // The OP returns a JWT containing the authorization code, state, etc.
                var request = ctx.HttpContext.Request;
                var response = request.HasFormContentType ? request.Form["response"].ToString() : request.Query["response"].ToString();

                if (!string.IsNullOrWhiteSpace(response))
                {
                    try
                    {
                        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                        var jwt = handler.ReadJwtToken(response);

                        // Extract claims from JWT and populate ProtocolMessage
                        ctx.ProtocolMessage.Code = jwt.Claims.FirstOrDefault(c => c.Type == "code")?.Value;
                        ctx.ProtocolMessage.State = jwt.Claims.FirstOrDefault(c => c.Type == "state")?.Value;
                        ctx.ProtocolMessage.Iss = jwt.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;

                        // Handle any error in the JWT
                        var error = jwt.Claims.FirstOrDefault(c => c.Type == "error")?.Value;
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            ctx.ProtocolMessage.Error = error;
                            ctx.ProtocolMessage.ErrorDescription = jwt.Claims.FirstOrDefault(c => c.Type == "error_description")?.Value;
                            ctx.ProtocolMessage.ErrorUri = jwt.Claims.FirstOrDefault(c => c.Type == "error_uri")?.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OIDC").LogError(ex, "Failed to decode JARM response");
                    }
                }

                return Task.CompletedTask;
            },
            OnRedirectToIdentityProvider = async ctx =>
            {
                try
                {
                    var config = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                    var privateJwk = config["Oidc:PrivateJwk"] ?? config["OIDC:PrivateJwk"];
                    var usePar = bool.TryParse(config["Oidc:UsePar"] ?? config["OIDC:UsePar"], out var parFlag) && parFlag;

                    if (!string.IsNullOrWhiteSpace(privateJwk) && usePar)
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

// Distributed cache and backchannel stores
var redisConn = builder.Configuration["Redis:Configuration"];
if (!string.IsNullOrWhiteSpace(redisConn))
{
    builder.Services.AddStackExchangeRedisCache(opts =>
    {
        opts.Configuration = redisConn;
    });
    builder.Services.AddSingleton<IRevocationStore, DistributedRevocationStore>();
    builder.Services.AddSingleton<IReplayCache, DistributedReplayCache>();
}
else
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton<IRevocationStore, MemoryRevocationStore>();
    builder.Services.AddSingleton<IReplayCache, MemoryReplayCache>();
}

// JWKS cache and validator
builder.Services.AddSingleton<IJwksCache, JwksCache>();
builder.Services.AddSingleton<IBackchannelConfigurationProvider, BackchannelConfigurationProvider>();
builder.Services.AddSingleton(sp =>
{
    // Bind options snapshot for validator
    var cfg = sp.GetRequiredService<IConfiguration>();
    var opts = new BackchannelOptions();
    cfg.GetSection("Backchannel").Bind(opts);
    var authority = cfg["Oidc:Authority"] ?? cfg["OIDC:Authority"] ?? string.Empty;
    var clientId = cfg["Oidc:ClientId"] ?? cfg["OIDC:ClientId"] ?? "blazor-web";
    opts.Enabled = true;
    opts.Authority = authority;
    opts.ClientId = clientId;
    return opts;
});
builder.Services.AddSingleton<LogoutTokenValidator>();

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

    var requested = ctx.Request.Query["returnUrl"].FirstOrDefault()
                   ?? ctx.Request.Query["redirectUri"].FirstOrDefault()
                   ?? "/";
    if (!requested.StartsWith('/')) requested = "/" + requested;

    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var authorityRaw = cfg["Oidc:Authority"] ?? cfg["OIDC:Authority"];
    var clientId = cfg["Oidc:ClientId"] ?? cfg["OIDC:ClientId"] ?? "blazor-web";

    if (string.IsNullOrWhiteSpace(authorityRaw) || !Uri.TryCreate(authorityRaw, UriKind.Absolute, out var authUri))
    {
        ctx.Response.Redirect(requested); return;
    }
    var normalizedAuthority = authUri.GetLeftPart(UriPartial.Authority) + authUri.AbsolutePath.TrimEnd('/') + "/";

    // Absolute post-logout redirect back to this RP (must be in client's AllowedLogoutRedirectUrisJson)
    var rpBase = ctx.Request.Scheme + "://" + ctx.Request.Host;
    var absoluteReturn = rpBase + requested; // e.g. https://localhost:7180/

    // Use standard OIDC RP-initiated logout (end_session_endpoint)
    var target = normalizedAuthority + "connect/endsession?client_id=" + Uri.EscapeDataString(clientId)
        + "&post_logout_redirect_uri=" + Uri.EscapeDataString(absoluteReturn);

    ctx.Response.Redirect(target);
}).ExcludeFromDescription();

// Backchannel logout receiver (per OIDC): accepts logout_token and invalidates sessions by sid
app.MapPost("/backchannel-logout", async ctx =>
{
    // Read form-encoded body
    if (!ctx.Request.HasFormContentType)
    {
        ctx.Response.StatusCode = 400; return;
    }
    if (ctx.Request.ContentLength is > 8192)
    {
        ctx.Response.StatusCode = 413; return; // Payload Too Large
    }
    var form = await ctx.Request.ReadFormAsync();
    var logoutToken = form["logout_token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(logoutToken)) { ctx.Response.StatusCode = 400; return; }
    var validator = ctx.RequestServices.GetRequiredService<LogoutTokenValidator>();
    var result = await validator.ValidateAsync(logoutToken, ctx.RequestAborted);
    if (!result.Success)
    {
        ctx.Response.StatusCode = 401; return;
    }

    var opts = ctx.RequestServices.GetRequiredService<BackchannelOptions>();
    var store = ctx.RequestServices.GetRequiredService<IRevocationStore>();
    if (string.IsNullOrEmpty(result.Sid))
    {
        // This sample RP only indexes sessions by sid. Reject sub-only logout tokens instead of returning a false success.
        ctx.Response.StatusCode = 400; return;
    }

    await store.RevokeSidAsync(result.Sid, opts.SidTtl, ctx.RequestAborted);

    ctx.Response.StatusCode = 200;
}).ExcludeFromDescription();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();

// Old in-memory BackchannelLogoutStore removed (replaced by IRevocationStore implementations)
