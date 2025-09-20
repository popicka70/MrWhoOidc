using MrWhoOidc.Web;
using MrWhoOidc.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Read Authority from configuration (required). Set Oidc:Authority (or OIDC:Authority) in appsettings or environment.
string? authority = builder.Configuration["Oidc:Authority"];

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
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException("Oidc:Authority must be configured to use OpenID Connect.");
        }

        options.Authority = authority;
        options.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        options.ClientId = builder.Configuration["Oidc:ClientId"] ?? "blazor-web";
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]; // optional for public client
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        // Allow HTTP metadata in dev when Authority is http:// by using a custom ConfigurationManager
        if (authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            options.MetadataAddress = metadataAddress;
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = false }
            );
        }

        options.TokenValidationParameters.ValidateIssuer = false; // dev only
    });

// Add services to the container.
builder.Services.AddAuthorization();

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
