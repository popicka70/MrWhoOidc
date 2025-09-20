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

// Create a dedicated backchannel HttpClient to control TLS/version behavior
static HttpClient CreateBackchannel()
{
    var handler = new SocketsHttpHandler
    {
        // Force HTTP/1.1 to avoid potential HTTP/2 ALPN/TLS negotiation issues
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions =
        {
            EnabledSslProtocols = SslProtocols.Tls12,
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true // dev only
        }
    };

    var client = new HttpClient(handler)
    {
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        Timeout = TimeSpan.FromSeconds(30)
    };
    return client;
}

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

        // Custom backchannel to control TLS/protocols
        options.Backchannel = CreateBackchannel();

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

        // Keep error logging without PII
        options.Events = new OpenIdConnectEvents
        {
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

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
