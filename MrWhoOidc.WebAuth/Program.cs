using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(oidcOptions);

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

// Persist DataProtection keys to the shared AuthDbContext so antiforgery keys survive restarts
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AuthDbContext>();

// Rate limiting policies
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("rl-authorize", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("rl-token", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("rl-userinfo", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// Register handlers
builder.Services.AddSingleton<IDiscoveryHandler, DiscoveryHandler>();
builder.Services.AddScoped<IAuthorizeHandler, AuthorizeHandler>();
builder.Services.AddSingleton<ILogoutHandler, LogoutHandler>();
builder.Services.AddScoped<ITokenHandler, TokenHandler>();
builder.Services.AddScoped<IUserInfoHandler, UserInfoHandler>();
builder.Services.AddScoped<IRevocationHandler, RevocationHandler>();

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
app.UseRateLimiter();

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

app.MapGet("/.well-known/openid-configuration", (IDiscoveryHandler h, HttpContext ctx) => h.Handle(ctx));
app.MapGet("/jwks", async (HttpContext ctx, IKeyStore keys, CancellationToken ct) =>
{
    var jwks = await keys.GetPublicJwksAsync(ct);
    ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
    return Results.Json(new { keys = jwks });
});
app.MapGet("/authorize", (IAuthorizeHandler h, HttpContext ctx) => h.HandleAsync(ctx))
   .RequireRateLimiting("rl-authorize");
app.MapGet("/logout", (ILogoutHandler h, HttpContext ctx) => h.LocalLogoutAsync(ctx));
app.MapGet("/connect/endsession", (ILogoutHandler h, HttpContext ctx) => h.EndSessionAsync(ctx));
app.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx))
   .RequireRateLimiting("rl-token");
app.MapPost("/revoke", (IRevocationHandler h, HttpContext ctx) => h.HandleAsync(ctx));
app.MapGet("/userinfo", (IUserInfoHandler h, HttpContext ctx) => h.Handle(ctx))
   .RequireRateLimiting("rl-userinfo");

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
