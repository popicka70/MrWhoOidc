using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Add Razor Pages
builder.Services.AddRazorPages();

// Configure OIDC authentication
var oidcSettings = builder.Configuration.GetSection("OidcSettings");
var authority = oidcSettings["Authority"];
var discoveryUri = oidcSettings["DiscoveryUri"];
var clientId = oidcSettings["ClientId"];
var clientSecret = oidcSettings["ClientSecret"];

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "MrWhoOidc.Demo";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = true;
})
.AddOpenIdConnect(options =>
{
    options.Authority = authority;
    options.MetadataAddress = string.IsNullOrWhiteSpace(discoveryUri) ? null : discoveryUri;
    options.ClientId = clientId;
    options.ClientSecret = clientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.RequireHttpsMetadata = true;
    options.UsePkce = true;

    // Disable Pushed Authorization Requests (PAR) - the IdP requires AdvancedSecurity license for PAR
    options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

    // Clear default scopes and add configured ones
    options.Scope.Clear();
    var scopes = oidcSettings.GetSection("Scopes").Get<string[]>() ?? ["openid", "profile", "email"];
    foreach (var scope in scopes)
    {
        options.Scope.Add(scope);
    }

    // Map claims for display
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        NameClaimType = "name",
        RoleClaimType = "role"
    };

    options.Events = new OpenIdConnectEvents
    {
        OnRemoteFailure = context =>
        {
            context.Response.Redirect($"/Error?message={Uri.EscapeDataString(context.Failure?.Message ?? "Authentication failed")}");
            context.HandleResponse();
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorPages();

app.Run();
