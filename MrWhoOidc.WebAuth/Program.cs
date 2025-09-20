using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorPages();

// Cookies for local login session
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".mrwhooidc.auth";
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

// Wire up Auth persistence (PostgreSQL via Aspire connection)
builder.Services.AddAuthPersistence(builder.Configuration);
// Register Auth core services
builder.Services.AddMrWhoOidcAuthCore();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Delay DB migration and seeding until after the host is fully started
app.Lifetime.ApplicationStarted.Register(() =>
{
    Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();

        // Ensure at least one signing key exists
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
        await keyStore.GetActiveSigningKeyAsync();

        // Seed default user and client
        await MrWhoOidc.Auth.Seeding.DatabaseSeeder.EnsureSeedDataAsync(app.Services);
    });
});

app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) => Results.Json(new
{
    issuer = $"{ctx.Request.Scheme}://{ctx.Request.Host}",
    authorization_endpoint = "/authorize",
    token_endpoint = "/token",
    userinfo_endpoint = "/userinfo",
    jwks_uri = "/jwks",
    response_types_supported = new[] { "code" },
    grant_types_supported = new[] { "authorization_code", "refresh_token" },
    code_challenge_methods_supported = new[] { "S256" },
    id_token_signing_alg_values_supported = new[] { "RS256" },
    scopes_supported = new[] { "openid", "profile", "email" }
}));

app.MapGet("/jwks", async (IKeyStore keys, CancellationToken ct) =>
{
    var jwks = await keys.GetPublicJwksAsync(ct);
    return Results.Json(new { keys = jwks });
});

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
