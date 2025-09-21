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
using MrWhoOidc.WebAuth.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// NOTE: don't request client certificates at TLS layer to avoid browser cert prompts.
// For mTLS on machine-to-machine callers, prefer certificate forwarding via a reverse proxy.

builder.AddServiceDefaults();

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(oidcOptions);

// Bind AuthOptions (API audiences)
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Client certificate forwarding (when behind proxy sending base64 cert header)
builder.Services.AddCertificateForwarding(options =>
{
    options.CertificateHeader = "X-Client-Cert";
});

// Add services to the container.
builder.Services.AddRazorPages();

// Metrics
builder.Services.AddSingleton<OidcMetrics>();

// Seed command support
builder.Services.AddScoped<ISeeder, Seeder>();

// CORS allow-list for OIDC endpoints (tighten to only required)
builder.Services.AddCors(options =>
{
    options.AddPolicy("oidc", policy =>
    {
        if (oidcOptions.AllowedCorsOrigins is { Length: > 0 })
        {
            policy.WithOrigins(oidcOptions.AllowedCorsOrigins)
                  .WithMethods("POST", "OPTIONS")
                  .WithHeaders("authorization", "content-type")
                  .DisallowCredentials();
        }
        else
        {
            policy.SetIsOriginAllowed(_ => false);
        }
    });
});

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

// Add private_key_jwt validator
builder.Services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();

// Register PAR handler
builder.Services.AddScoped<IParHandler, ParHandler>();

// DPoP services
builder.Services.AddSingleton<IDPoPValidator, DPoPValidator>();
var redisConnection = builder.Configuration.GetConnectionString("redis") ?? builder.Configuration["ConnectionStrings:redis"];
IConnectionMultiplexer? redisMux = null;
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    redisMux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
    builder.Services.AddSingleton(redisMux);
    builder.Services.AddSingleton<IDPoPReplayCache, RedisDPoPReplayCache>();
    builder.Services.AddSingleton<IDPoPNonceStore, RedisDPoPNonceStore>();
}
else
{
    builder.Services.AddSingleton<IDPoPReplayCache, InMemoryDPoPReplayCache>();
    builder.Services.AddSingleton<IDPoPNonceStore, InMemoryDPoPNonceStore>();
}

// Persist DataProtection keys to the shared AuthDbContext so antiforgery keys survive restarts
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AuthDbContext>();

// Rate limiting policies using distributed store (Redis)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    if (redisMux is not null)
    {
        var limiterOptions = new RedisFixedWindowRateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1), Prefix = "rl" };
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = limiterOptions.PermitLimit,
                QueueLimit = 0,
                TokensPerPeriod = limiterOptions.PermitLimit,
                ReplenishmentPeriod = limiterOptions.Window,
                AutoReplenishment = true
            });
        });
    }

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

    options.AddPolicy("rl-par", httpContext =>
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

    // Introspection is similar sensitivity to token; rate limit appropriately
    options.AddPolicy("rl-introspect", httpContext =>
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
});

// Register handlers
builder.Services.AddSingleton<IDiscoveryHandler, DiscoveryHandler>();
builder.Services.AddScoped<IAuthorizeHandler, AuthorizeHandler>();
builder.Services.AddSingleton<ILogoutHandler, LogoutHandler>();
builder.Services.AddScoped<ITokenHandler, TokenHandler>();
builder.Services.AddScoped<IUserInfoHandler, UserInfoHandler>();
builder.Services.AddScoped<IRevocationHandler, RevocationHandler>();
// Introspection
builder.Services.AddScoped<IIntrospectionHandler, IntrospectionHandler>();

var app = builder.Build();

// Support: dotnet run -- --seed
if (args.Contains("--seed", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
    var seeder = scope.ServiceProvider.GetRequiredService<ISeeder>();
    await seeder.SeedAsync();
    return; // exit after seeding
}

app.MapDefaultEndpoints();

// Trust proxy forwarded headers
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Forward client certificates from proxy header if present
app.UseCertificateForwarding();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
else
{
    // Enable HTTPS redirection optionally for dev if desired
    // app.UseHttpsRedirection();
}

app.UseRouting();
app.UseCors();
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

app.MapGet("/.well-known/openid-configuration", (IDiscoveryHandler h, HttpContext ctx) => h.Handle(ctx))
   .RequireRateLimiting("rl-authorize");
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
   .RequireCors("oidc")
   .RequireRateLimiting("rl-token");
app.MapMethods("/token", new[] { "OPTIONS" }, () => Results.Ok())
   .RequireCors("oidc");
app.MapPost("/revoke", (IRevocationHandler h, HttpContext ctx) => h.HandleAsync(ctx));
app.MapGet("/userinfo", (IUserInfoHandler h, HttpContext ctx) => h.Handle(ctx))
   .RequireCors("oidc")
   .RequireRateLimiting("rl-userinfo");
app.MapMethods("/userinfo", new[] { "OPTIONS" }, () => Results.Ok())
   .RequireCors("oidc");

// Introspection endpoint
app.MapPost("/introspect", (IIntrospectionHandler h, HttpContext ctx) => h.HandleAsync(ctx))
   .RequireRateLimiting("rl-introspect");

// PAR endpoint
app.MapPost("/par", (IParHandler h, HttpContext ctx) => h.HandleAsync(ctx))
   .RequireCors("oidc")
   .RequireRateLimiting("rl-par");
app.MapMethods("/par", new[] { "OPTIONS" }, () => Results.Ok())
   .RequireCors("oidc");

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
