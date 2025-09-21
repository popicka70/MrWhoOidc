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

                    // Add request_uri; keep other params so state/correlation remains intact (server sanitizes URL)
                    ctx.ProtocolMessage.Parameters["request_uri"] = requestUri;
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
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = ctx.Request.Query["returnUrl"].FirstOrDefault() ?? "/"
    });
}).ExcludeFromDescription();

// JAR + PAR helper endpoint (now uses Challenge so OIDC sets correlation/state cookies)
app.MapPost("/auth/jar", async (HttpContext ctx) =>
{
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = ctx.Request.Query["returnUrl"].FirstOrDefault() ?? "/"
    });
    return Results.Empty;
}).DisableAntiforgery().ExcludeFromDescription();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
