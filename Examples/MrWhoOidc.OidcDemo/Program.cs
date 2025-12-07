using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Add Razor Pages
builder.Services.AddRazorPages();

// Configure OIDC authentication
var oidcSettings = builder.Configuration.GetSection("OidcSettings");

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
    options.Authority = oidcSettings["Authority"];
    options.ClientId = oidcSettings["ClientId"];
    options.ClientSecret = oidcSettings["ClientSecret"];
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

app.MapRazorPages();

app.Run();
