using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth; // Add extension methods namespace
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Text;
using MrWhoOidc.WebAuth.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.Security;
using Microsoft.AspNetCore.Authorization;
using MrWhoOidc.WebAuth.Security;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using MrWhoOidc.WebAuth.Background;

var builder = WebApplication.CreateBuilder(args);

// NOTE: don't request client certificates at TLS layer to avoid browser cert prompts.
// For mTLS on machine-to-machine callers, prefer certificate forwarding via a reverse proxy.

builder.AddServiceDefaults();

// Application Insights (optional): only wires if instrumentation key / connection string present
var aiConn = builder.Configuration["ApplicationInsights:ConnectionString"] ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(aiConn))
{
    builder.Services.AddApplicationInsightsTelemetry(o =>
    {
        o.ConnectionString = aiConn;
    });
}

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(oidcOptions);

// Bind AuthOptions (API audiences)
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Admin policy options
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));

// Client certificate forwarding (when behind proxy sending base64 cert header)
builder.Services.AddCertificateForwarding(options =>
{
    options.CertificateHeader = "X-Client-Cert";
});

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    // Authorize entire Admin folder with the 'admin' policy
    options.Conventions.AuthorizeFolder("/Admin", "admin");
});

// Localization for friendly external OIDC error pages (initial: en-US only; extensible later)
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Metrics
builder.Services.AddSingleton<OidcMetrics>();
builder.Services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
// Safety: ensure at least one implementation exists (tests that construct very slim hosts may miss this)
if (!builder.Services.Any(d => d.ServiceType == typeof(ITokenMetricsRecorder)))
{
    builder.Services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
}
// Token-exchange rate limiting
builder.Services.Configure<MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.TokenExchangeRateLimitOptions>(builder.Configuration.GetSection("TokenExchangeRateLimit"));
// Default to in-memory; override with Redis below if available
builder.Services.AddSingleton<MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.ITokenExchangeRateLimiter, MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.InMemoryTokenExchangeRateLimiter>();
// Alerting
builder.Services.AddHttpClient();
// Provide system clock abstraction for alert sampler
builder.Services.AddSingleton<MrWhoOidc.WebAuth.Background.ISystemClock, MrWhoOidc.WebAuth.Background.SystemClock>();
builder.Services.AddSingleton<IAlertPublisher>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var hasWebhook = !string.IsNullOrWhiteSpace(cfg["Backchannel:AlertWebhook"]);
    return hasWebhook ? new WebhookAlertPublisher(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<WebhookAlertPublisher>>(), cfg) : new NoopAlertPublisher();
});
// Backchannel alert sampler (threshold evaluation)
builder.Services.Configure<MrWhoOidc.WebAuth.Background.BackchannelAlertOptions>(builder.Configuration.GetSection("Backchannel:Alerts"));
builder.Services.AddHostedService<MrWhoOidc.WebAuth.Background.BackchannelAlertSampler>();
// Expose diagnostics interface for sampler
builder.Services.AddSingleton<IBackchannelAlertDiagnostics>(sp => (IBackchannelAlertDiagnostics)sp.GetRequiredService<BackchannelAlertSampler>());
// Audit sink (supports logger | appinsights | both)
builder.Services.Configure<MrWhoOidc.WebAuth.Observability.AuditOptions>(builder.Configuration.GetSection("Audit"));
builder.Services.AddSingleton<MrWhoOidc.WebAuth.Observability.IAuditSink>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MrWhoOidc.WebAuth.Observability.AuditOptions>>().Value;
    if (!opts.Enabled)
        return new MrWhoOidc.WebAuth.Observability.NoopAuditSink();

    var sinks = new List<MrWhoOidc.WebAuth.Observability.IAuditSink>();
    var sink = opts.Sink?.ToLowerInvariant() ?? "logger";
    if (sink is "logger" or "both")
    {
        sinks.Add(new MrWhoOidc.WebAuth.Observability.LoggerAuditSink(
            sp.GetRequiredService<ILogger<MrWhoOidc.WebAuth.Observability.LoggerAuditSink>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MrWhoOidc.WebAuth.Observability.AuditOptions>>()));
    }
    if (sink is "appinsights" or "both")
    {
        // TelemetryClient is auto-registered when Microsoft.ApplicationInsights.AspNetCore is referenced & AddApplicationInsightsTelemetry called.
        var telemetry = sp.GetService<Microsoft.ApplicationInsights.TelemetryClient>();
        if (telemetry != null)
        {
            sinks.Add(new MrWhoOidc.WebAuth.Observability.ApplicationInsightsAuditSink(
                telemetry,
                sp.GetRequiredService<ILogger<MrWhoOidc.WebAuth.Observability.ApplicationInsightsAuditSink>>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MrWhoOidc.WebAuth.Observability.AuditOptions>>()));
        }
        else if (sink != "logger")
        {
            // Fallback to logger if App Insights not configured
            sinks.Add(new MrWhoOidc.WebAuth.Observability.LoggerAuditSink(
                sp.GetRequiredService<ILogger<MrWhoOidc.WebAuth.Observability.LoggerAuditSink>>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MrWhoOidc.WebAuth.Observability.AuditOptions>>()));
        }
    }

    if (sinks.Count == 1)
        return sinks[0];
    if (sinks.Count == 0)
        return new MrWhoOidc.WebAuth.Observability.NoopAuditSink();
    return new MrWhoOidc.WebAuth.Observability.CompositeAuditSink(sinks);
});

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
    })
    .AddCookie("preauth", options =>
    {
        options.Cookie.Name = ".mrwhooidc.preauth";
        options.LoginPath = "/login";
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    });

// Authorization + admin policy
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.Requirements.Add(new AdminRequirement()));
});
builder.Services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();

// Wire up Auth persistence (PostgreSQL via Aspire connection)
builder.Services.AddAuthPersistence(builder.Configuration);
// Register Auth core services
builder.Services.AddMrWhoOidcAuthCore();

// HttpClient + IdP validator
builder.Services.AddHttpClient();
builder.Services.AddScoped<IIdentityProviderValidator, IdentityProviderValidator>();

// Add private_key_jwt validator
builder.Services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();

// Register PAR handler
builder.Services.AddScoped<IParHandler, ParHandler>();

// External OIDC chaining
builder.Services.AddScoped<IExternalOidcHandler, ExternalOidcHandler>();
builder.Services.AddSingleton<IJwksCache, JwksCache>();
// Claim mapping service
builder.Services.AddScoped<IClaimMappingService, ClaimMappingService>();

// DPoP services (use shared Security implementation)
builder.Services.AddSingleton<MrWhoOidc.Security.IDPoPValidator, MrWhoOidc.Security.DPoPValidator>();
var redisConnection = builder.Configuration.GetConnectionString("redis") ?? builder.Configuration["ConnectionStrings:redis"];
IConnectionMultiplexer? redisMux = null;
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    redisMux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
    builder.Services.AddSingleton(redisMux);
    builder.Services.AddSingleton<MrWhoOidc.Security.IDPoPReplayCache, RedisDPoPReplayCache>();
    builder.Services.AddSingleton<MrWhoOidc.Security.IDPoPNonceStore, RedisDPoPNonceStore>();
    // JAR replay cache: override in-memory default with Redis when available
    builder.Services.AddSingleton<IJarReplayCache, RedisJarReplayCache>();
    // Override TE rate limiter with Redis implementation when Redis is present
    builder.Services.AddSingleton<MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.ITokenExchangeRateLimiter, MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting.RedisTokenExchangeRateLimiter>();
}
else
{
    builder.Services.AddSingleton<MrWhoOidc.Security.IDPoPReplayCache, MrWhoOidc.Security.InMemoryDPoPReplayCache>();
    builder.Services.AddSingleton<MrWhoOidc.Security.IDPoPNonceStore, MrWhoOidc.Security.InMemoryDPoPNonceStore>();
}

// Persist DataProtection keys to the shared AuthDbContext so antiforgery keys survive restarts
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AuthDbContext>();

// Antiforgery tokens (used by interactive logout and future forms)
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = ".mrwhooidc.af"; // short, distinct
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax; // form posts are same-site
    options.FormFieldName = "__RequestVerificationToken"; // default; explicit for clarity
    options.HeaderName = "X-CSRF-TOKEN"; // allow JS-enhanced posts if needed later
});

// Background cleanup for expired tokens (opaque + refresh)
builder.Services.AddHostedService<ExpiredTokenCleanupService>();
// PAR cleanup
builder.Services.AddHostedService<ParCleanupHostedService>();
// BCL outbox dispatcher
builder.Services.AddSingleton(new BackchannelDispatchOptions());
builder.Services.Configure<MrWhoOidc.WebAuth.Background.BackchannelFeatureOptions>(builder.Configuration.GetSection("Backchannel"));
builder.Services.AddSingleton<MrWhoOidc.WebAuth.Background.BackchannelRuntimeState>();
builder.Services.AddHostedService<BackchannelLogoutDispatcher>();

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

    // Dedicated policy for Token Exchange requests (can be tuned separately)
    options.AddPolicy("rl-token-exchange", httpContext =>
    {
        // Partition by client_id when present to avoid penalizing all clients by IP
        string key = "unknown";
        if (httpContext.Request.HasFormContentType)
        {
            try
            {
                var form = httpContext.Request.ReadFormAsync().GetAwaiter().GetResult();
                string? cidFromHeader = null;
                var header = httpContext.Request.Headers.Authorization.ToString();
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.Ordinal))
                {
                    try
                    {
                        var raw = header.Substring("Basic ".Length).Trim();
                        var bytes = Convert.FromBase64String(raw);
                        var pair = Encoding.UTF8.GetString(bytes);
                        var idx = pair.IndexOf(':');
                        if (idx >= 0) cidFromHeader = pair[..idx];
                    }
                    catch { }
                }
                var cid = !string.IsNullOrEmpty(cidFromHeader) ? cidFromHeader : form["client_id"].ToString();
                key = !string.IsNullOrEmpty(cid) ? cid : (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            }
            catch { key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"; }
        }
        else
        {
            key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
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

    // Admin endpoints policy (more restrictive by default)
    options.AddPolicy("rl-admin", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, // adjust per environment
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// Register handlers
builder.Services.AddScoped<IDiscoveryHandler, DiscoveryHandler>();
builder.Services.AddScoped<IAuthorizeHandler, AuthorizeHandler>();
builder.Services.AddScoped<ILogoutHandler, LogoutHandler>();
// Lifetime fix: service uses AuthDbContext (scoped) so must not be singleton
builder.Services.AddScoped<IUpstreamLogoutService, UpstreamLogoutService>();
builder.Services.AddMemoryCache();
builder.Services.Configure<FederatedLogoutOptions>(builder.Configuration.GetSection("FederatedLogout"));
builder.Services.AddScoped<ITokenHandler, TokenHandler>();
// Grant handlers (strategy pattern pilot)
builder.Services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.RefreshTokenGrantHandler>();
builder.Services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.AuthorizationCodeGrantHandler>();
builder.Services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.ClientCredentialsGrantHandler>();
builder.Services.AddScoped<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ITokenGrantHandler, MrWhoOidc.WebAuth.TokenEndpoint.Grants.TokenExchangeGrantHandler>();
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

// Trust proxy forwarded headers (needed for TLS termination behind a reverse proxy like Render)
// This ensures Request.Scheme/Host reflect the original client-facing values so discovery publishes https URLs.
var fwdOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
// When running behind a managed proxy (IPs may change), clear KnownNetworks/Proxies to accept the headers.
// IMPORTANT: Only do this when the app isn't directly internet-exposed without a reverse proxy.
fwdOptions.KnownNetworks.Clear();
fwdOptions.KnownProxies.Clear();
app.UseForwardedHeaders(fwdOptions);

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
// Request localization (default culture en-US; future: read from configuration or user preference)
var supportedCultures = new[] { "en-US" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0])
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);
app.UseRequestLocalization(localizationOptions);
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
// Distributed limiter (Redis-backed) to add Retry-After and shared limits
if (redisMux is not null)
{
    app.UseMiddleware<DistributedRateLimiterMiddleware>();
}
app.UseRateLimiter();

app.MapRazorPages().WithStaticAssets();

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
// Federated logout entry (GET displays choice; POST processes; fallback to local if disabled)
app.MapGet("/logout", (ILogoutHandler h, HttpContext ctx) => h.LogoutEntryAsync(ctx));
// POST moved into Razor Page (/Pages/Logout/Prompt/Index.cshtml.cs OnPost)
app.MapGet("/logout/federated-callback", (ILogoutHandler h, HttpContext ctx) => h.FederatedCallbackAsync(ctx));
app.MapGet("/connect/endsession", (ILogoutHandler h, HttpContext ctx) => h.EndSessionAsync(ctx));
app.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx))
   .RequireCors("oidc")
    .RequireRateLimiting("rl-token")
    .RequireRateLimiting("rl-token-exchange");
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

// External OIDC chaining endpoints
app.MapGet("/Auth/External/Start", (IExternalOidcHandler h, HttpContext ctx) => h.StartAsync(ctx));
app.MapGet("/Auth/External/Callback", (IExternalOidcHandler h, HttpContext ctx) => h.CallbackAsync(ctx));
app.MapGet("/Auth/External/Confirm", (IExternalOidcHandler h, HttpContext ctx) => h.ConfirmLinkAsync(ctx));

// Admin Management APIs (admin-only, ProblemDetails on errors)
var admin = app.MapGroup("/admin/api").RequireAuthorization("admin").RequireRateLimiting("rl-admin");

// Providers CRUD
admin.MapGet("/providers", async (AuthDbContext db, CancellationToken ct) =>
{
    var list = await db.IdentityProviders.AsNoTracking()
        .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
        .Select(p => new
        {
            p.Id,
            p.Name,
            p.DisplayName,
            p.Type,
            p.Enabled,
            p.IsDefault,
            p.LogoUrl,
            p.SortOrder,
            p.CreatedAt,
            p.UpdatedAt
        }).ToListAsync(ct);
    return Results.Ok(list);
});

admin.MapGet("/providers/{id:guid}", async (Guid id, AuthDbContext db, CancellationToken ct) =>
{
    var p = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    return p is null ? Results.Problem(statusCode: 404, title: "Not Found") : Results.Ok(p);
});

admin.MapPost("/providers", async (AuthDbContext db, IIdentityProviderValidator validator, MrWhoOidc.Auth.Persistence.IdentityProvider input, CancellationToken ct) =>
{
    input.Id = Guid.NewGuid();
    input.CreatedAt = DateTimeOffset.UtcNow;
    input.UpdatedAt = DateTimeOffset.UtcNow;
    var (ok, error) = await validator.ValidateAsync(input, ct);
    if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

    db.IdentityProviders.Add(input);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/admin/api/providers/{input.Id}", new { input.Id });
});

admin.MapPut("/providers/{id:guid}", async (Guid id, AuthDbContext db, IIdentityProviderValidator validator, MrWhoOidc.Auth.Persistence.IdentityProvider input, CancellationToken ct) =>
{
    var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

    // Update fields
    entity.Name = input.Name;
    entity.DisplayName = input.DisplayName;
    entity.Type = input.Type;
    entity.Enabled = input.Enabled;
    entity.IsDefault = input.IsDefault;
    entity.LogoUrl = input.LogoUrl;
    entity.SortOrder = input.SortOrder;
    entity.ConfigJson = input.ConfigJson;
    entity.UpdatedAt = DateTimeOffset.UtcNow;

    var (ok, error) = await validator.ValidateAsync(entity, ct);
    if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapDelete("/providers/{id:guid}", async (Guid id, AuthDbContext db, CancellationToken ct) =>
{
    var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    db.IdentityProviders.Remove(entity);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Client ? Providers mapping CRUD
admin.MapGet("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
{
    var list = await db.ClientIdentityProviders.AsNoTracking()
        .Where(m => m.ClientId == clientId)
        .Join(db.IdentityProviders.AsNoTracking(), m => m.IdentityProviderId, p => p.Id, (m, p) => new
        {
            m.ClientId,
            m.IdentityProviderId,
            p.Name,
            p.DisplayName,
            m.Enabled,
            m.IsDefaultForClient,
            m.AutoRedirectIfSingle,
            m.RequiredAcr,
            m.Order
        })
        .OrderBy(x => x.Order).ToListAsync(ct);
    return Results.Ok(list);
});

admin.MapPost("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, MrWhoOidc.WebAuth.Security.MappingInput input, CancellationToken ct) =>
{
    if (input is null || input.IdentityProviderId == Guid.Empty)
        return Results.Problem(statusCode: 400, title: "Invalid input");

    // Ensure client and provider exist
    var clientExists = await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
    var providerExists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == input.IdentityProviderId, ct);
    if (!clientExists || !providerExists)
        return Results.Problem(statusCode: 404, title: "Client or Provider not found");

    var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, input.IdentityProviderId }, ct);
    if (entity is null)
    {
        entity = new ClientIdentityProvider
        {
            ClientId = clientId,
            IdentityProviderId = input.IdentityProviderId,
            Enabled = input.Enabled,
            IsDefaultForClient = input.IsDefaultForClient,
            AutoRedirectIfSingle = input.AutoRedirectIfSingle,
            RequiredAcr = input.RequiredAcr,
            Order = input.Order
        };
        db.ClientIdentityProviders.Add(entity);
    }
    else
    {
        entity.Enabled = input.Enabled;
        entity.IsDefaultForClient = input.IsDefaultForClient;
        entity.AutoRedirectIfSingle = input.AutoRedirectIfSingle;
        entity.RequiredAcr = input.RequiredAcr;
        entity.Order = input.Order;
    }

    if (input.IsDefaultForClient)
    {
        var others = await db.ClientIdentityProviders.Where(m => m.ClientId == clientId && m.IdentityProviderId != input.IdentityProviderId).ToListAsync(ct);
        foreach (var o in others) o.IsDefaultForClient = false;
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok();
});

admin.MapPut("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, MrWhoOidc.WebAuth.Security.MappingInput input, CancellationToken ct) =>
{
    var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

    entity.Enabled = input.Enabled;
    entity.IsDefaultForClient = input.IsDefaultForClient;
    entity.AutoRedirectIfSingle = input.AutoRedirectIfSingle;
    entity.RequiredAcr = input.RequiredAcr;
    entity.Order = input.Order;

    if (input.IsDefaultForClient)
    {
        var others = await db.ClientIdentityProviders.Where(m => m.ClientId == clientId && m.IdentityProviderId != identityProviderId).ToListAsync(ct);
        foreach (var o in others) o.IsDefaultForClient = false;
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapDelete("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, CancellationToken ct) =>
{
    var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    db.ClientIdentityProviders.Remove(entity);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Claim mappings CRUD
admin.MapGet("/providers/{providerId:guid}/claim-mappings", async (Guid providerId, AuthDbContext db, CancellationToken ct) =>
{
    var list = await db.IdentityProviderClaimMappings.AsNoTracking()
        .Where(m => m.IdentityProviderId == providerId)
        .OrderBy(m => m.Order)
        .Select(m => new { m.Id, m.IdentityProviderId, m.ExternalClaim, m.LocalClaim, m.Transform, m.Order })
        .ToListAsync(ct);
    return Results.Ok(list);
});

admin.MapPost("/providers/{providerId:guid}/claim-mappings", async (Guid providerId, AuthDbContext db, MrWhoOidc.WebAuth.Security.ClaimMappingInput input, CancellationToken ct) =>
{
    if (input is null || string.IsNullOrWhiteSpace(input.ExternalClaim) || string.IsNullOrWhiteSpace(input.LocalClaim))
        return Results.Problem(statusCode: 400, title: "Invalid input");
    var exists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct);
    if (!exists) return Results.Problem(statusCode: 404, title: "Provider not found");

    var entity = new IdentityProviderClaimMapping
    {
        IdentityProviderId = providerId,
        ExternalClaim = input.ExternalClaim!,
        LocalClaim = input.LocalClaim!,
        Transform = input.Transform,
        Order = input.Order
    };
    db.IdentityProviderClaimMappings.Add(entity);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/admin/api/providers/{providerId}/claim-mappings/{entity.Id}", new { entity.Id });
});

admin.MapPut("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, MrWhoOidc.WebAuth.Security.ClaimMappingInput input, CancellationToken ct) =>
{
    var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id && m.IdentityProviderId == providerId, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    if (string.IsNullOrWhiteSpace(input.ExternalClaim) || string.IsNullOrWhiteSpace(input.LocalClaim))
        return Results.Problem(statusCode: 400, title: "Invalid input");

    entity.ExternalClaim = input.ExternalClaim!;
    entity.LocalClaim = input.LocalClaim!;
    entity.Transform = input.Transform;
    entity.Order = input.Order;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapDelete("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, CancellationToken ct) =>
{
    var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id && m.IdentityProviderId == providerId, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    db.IdentityProviderClaimMappings.Remove(entity);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Provider keys CRUD
admin.MapGet("/providers/{providerId:guid}/keys", async (Guid providerId, AuthDbContext db, CancellationToken ct) =>
{
    var list = await db.IdentityProviderKeys.AsNoTracking()
        .Where(k => k.IdentityProviderId == providerId)
        .OrderByDescending(k => k.CreatedAt)
        .Select(k => new { k.Id, k.Purpose, k.Alg, k.Kid, k.Active, k.CreatedAt, k.ExpiresAt })
        .ToListAsync(ct);
    return Results.Ok(list);
});

admin.MapPost("/providers/{providerId:guid}/keys", async (Guid providerId, AuthDbContext db, MrWhoOidc.WebAuth.Security.ProviderKeyInput input, CancellationToken ct) =>
{
    if (input is null || string.IsNullOrWhiteSpace(input.JwkJson) || string.IsNullOrWhiteSpace(input.Alg))
        return Results.Problem(statusCode: 400, title: "Invalid input");

    // Validate JSON shape
    try { using var _ = JsonDocument.Parse(input.JwkJson!); }
    catch (Exception ex) { return Results.Problem(statusCode: 400, title: "Invalid JWK JSON", detail: ex.Message); }

    // kid uniqueness per provider
    if (!string.IsNullOrWhiteSpace(input.Kid))
    {
        var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == input.Kid, ct);
        if (kidExists) return Results.Problem(statusCode: 409, title: "Duplicate kid", detail: "Key ID already exists for this provider.");
    }

    var entity = new IdentityProviderKey
    {
        IdentityProviderId = providerId,
        Purpose = input.Purpose,
        Jwk = input.JwkJson!,
        Alg = input.Alg!,
        Active = input.Active,
        Kid = string.IsNullOrWhiteSpace(input.Kid) ? Guid.NewGuid().ToString("N") : input.Kid,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = input.ExpiresAt
    };
    db.IdentityProviderKeys.Add(entity);

    if (entity.Active)
    {
        var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync(ct);
        foreach (var o in others) o.Active = false;
    }
    await db.SaveChangesAsync(ct);
    return Results.Created($"/admin/api/providers/{providerId}/keys/{entity.Id}", new { entity.Id });
});

admin.MapPut("/providers/{providerId:guid}/keys/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, MrWhoOidc.WebAuth.Security.ProviderKeyInput input, CancellationToken ct) =>
{
    var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    if (input is null || string.IsNullOrWhiteSpace(input.Alg)) return Results.Problem(statusCode: 400, title: "Invalid input");

    if (!string.IsNullOrWhiteSpace(input.JwkJson))
    {
        try { using var _ = JsonDocument.Parse(input.JwkJson!); }
        catch (Exception ex) { return Results.Problem(statusCode: 400, title: "Invalid JWK JSON", detail: ex.Message); }
        entity.Jwk = input.JwkJson!;
    }

    if (!string.IsNullOrWhiteSpace(input.Kid) && !string.Equals(input.Kid, entity.Kid, StringComparison.Ordinal))
    {
        var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == input.Kid, ct);
        if (kidExists) return Results.Problem(statusCode: 409, title: "Duplicate kid", detail: "Key ID already exists for this provider.");
        entity.Kid = input.Kid;
    }

    entity.Purpose = input.Purpose;
    entity.Alg = input.Alg!;
    entity.Active = input.Active;
    entity.ExpiresAt = input.ExpiresAt;

    if (entity.Active)
    {
        var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync(ct);
        foreach (var o in others) o.Active = false;
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapDelete("/providers/{providerId:guid}/keys/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, CancellationToken ct) =>
{
    var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    db.IdentityProviderKeys.Remove(entity);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Client keys (JWKS) read/update
admin.MapGet("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
{
    var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
    if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");

    var history = await db.ClientJwksHistories.AsNoTracking()
        .Where(h => h.ClientId == clientId)
        .OrderByDescending(h => h.CreatedAt)
        .Select(h => new { h.Id, h.CreatedAt, h.Source, h.Hash })
        .ToListAsync(ct);

    return Results.Ok(new { client.PublicJwksJson, client.PublicJwksUri, History = history });
});

admin.MapPut("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, MrWhoOidc.WebAuth.Security.ClientKeysInput input, CancellationToken ct) =>
{
    var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
    if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");

    if (!string.IsNullOrWhiteSpace(input.PublicJwksJson))
    {
        var status = MrWhoOidc.WebAuth.Security.AdminApiHelpers.ComputeJwksStatus(input.PublicJwksJson);
        if (status is { Ok: false })
            return Results.Problem(statusCode: 400, title: "Invalid JWKS", detail: status!.Value.Message);
        client.PublicJwksJson = input.PublicJwksJson;
        db.ClientJwksHistories.Add(new ClientJwksHistory
        {
            ClientId = client.Id,
            JwksJson = client.PublicJwksJson!,
            Source = "manual",
            Hash = MrWhoOidc.WebAuth.Security.AdminApiHelpers.ComputeSha256Hex(MrWhoOidc.WebAuth.Security.AdminApiHelpers.CompactJson(client.PublicJwksJson!))
        });
    }
    else
    {
        client.PublicJwksJson = null;
    }
    client.PublicJwksUri = string.IsNullOrWhiteSpace(input.PublicJwksUri) ? null : input.PublicJwksUri;

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// BCL outbox admin endpoints
admin.MapGet("/bcl/alerts/snapshot", (IBackchannelAlertDiagnostics diag) => Results.Ok(diag.GetSnapshot()));
admin.MapGet("/bcl/outbox", async (AuthDbContext db, MrWhoOidc.WebAuth.Observability.IAuditSink audit, HttpContext httpContext, int? take, string? status, CancellationToken ct) =>
{
    var q = db.BackchannelLogoutNotifications.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(status)) q = q.Where(n => n.Status == status);
    var list = await q.OrderByDescending(n => n.CreatedAt)
        .Take(Math.Clamp(take ?? 100, 1, 1000))
        .Select(n => new { n.Id, n.ClientId, n.TargetUri, n.Status, n.AttemptCount, n.MaxAttempts, n.LastHttpStatus, n.LastError, n.CreatedAt, n.LastAttemptAt, n.NextAttemptAt })
        .ToListAsync(ct);
    var backlog = await db.BackchannelLogoutNotifications.CountAsync(n => n.Status == "pending", ct);
    audit.Emit("bcl.admin.outbox.list", new { count = list.Count, backlog, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
    return Results.Ok(new { backlog, items = list });
});

admin.MapPost("/bcl/outbox/{id:guid}/retry", async (Guid id, AuthDbContext db, MrWhoOidc.WebAuth.Observability.IAuditSink audit, HttpContext httpContext, CancellationToken ct) =>
{
    var n = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(n => n.Id == id, ct);
    if (n is null) return Results.Problem(statusCode: 404, title: "Not Found");
    n.Status = "pending";
    n.NextAttemptAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    audit.Emit("bcl.admin.outbox.retry", new { id = n.Id, client_id = n.ClientId, target = new Uri(n.TargetUri).Host, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
    return Results.NoContent();
});

// Lightweight health endpoint for BCL dispatcher
app.MapGet("/health/backchannel", async (AuthDbContext db, MrWhoOidc.WebAuth.Background.BackchannelRuntimeState state, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var backlog = await db.BackchannelLogoutNotifications
        .AsNoTracking()
        .LongCountAsync(n => n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now), ct);

    // Top circuits (open ones only)
    var openCircuits = state.Circuits
        .Where(kv => kv.Value.OpenUntil is not null && kv.Value.OpenUntil > DateTimeOffset.UtcNow)
        .Select(kv => new { clientId = kv.Key, kv.Value.Failures, kv.Value.OpenUntil })
        .OrderByDescending(x => x.Failures)
        .Take(20)
        .ToList();

    return Results.Ok(new
    {
        enabled = state.EmissionEnabled,
        backlog,
        openCircuits,
    });
}).WithName("BackchannelHealth");

app.MapStaticAssets();

app.Run();

namespace MrWhoOidc.WebAuth.Security
{
    public sealed class AdminAuthOptions
    {
        public string RealmName { get; set; } = "admin";
        public string AdminRoleName { get; set; } = "admin";
    }

    public sealed class AdminRequirement : IAuthorizationRequirement { }

    public sealed class AdminAuthorizationHandler(AuthDbContext db, Microsoft.Extensions.Options.IOptions<AdminAuthOptions> options) : AuthorizationHandler<AdminRequirement>
    {
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
        {
            var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(sub, out var userId))
                return;

            var realmName = options.Value.RealmName;
            var roleName = options.Value.AdminRoleName;

            // Check active assignment of the admin role in the configured realm
            var hasAdmin = await db.UserRoleAssignments.AsNoTracking()
                .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                .Join(db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
                .AnyAsync(x => x.a.UserId == userId && x.a.IsActive && x.r.IsActive
                               && x.r.Name == roleName && x.rl.Name == realmName);

            if (hasAdmin)
                context.Succeed(requirement);
        }
    }

    // Input DTOs and helper methods for admin APIs
    public sealed record MappingInput(Guid IdentityProviderId, bool Enabled, bool IsDefaultForClient, bool AutoRedirectIfSingle, string? RequiredAcr, int Order);
    public sealed record ClaimMappingInput(string ExternalClaim, string LocalClaim, string? Transform, int Order);
    public sealed record ProviderKeyInput(MrWhoOidc.Auth.Persistence.IdentityProviderKeyPurpose Purpose, string Alg, string? Kid, bool Active, string JwkJson, DateTimeOffset? ExpiresAt);
    public sealed record ClientKeysInput(string? PublicJwksJson, string? PublicJwksUri);

    internal static class AdminApiHelpers
    {
        public static string CompactJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
        }

        public static string ComputeSha256Hex(string input)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static (bool Ok, string Summary, string? Message, int KeyCount, int UniqueKidCount, List<string> DuplicateKids)? ComputeJwksStatus(string? jwksJson)
        {
            if (string.IsNullOrWhiteSpace(jwksJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(jwksJson);
                var keys = new List<JsonElement>();
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
                {
                    keys = keysArr.EnumerateArray().ToList();
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    keys.Add(doc.RootElement);
                }
                else
                {
                    return (false, "Invalid", "JWKS must be an object with 'keys' array or a single JWK object.", 0, 0, new());
                }

                var count = keys.Count;
                var kids = keys.Select(k => k.TryGetProperty("kid", out var kid) ? kid.GetString() : null).ToList();
                var nonNullKids = kids.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                var dup = nonNullKids.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key!).ToList();

                var ok = dup.Count == 0;
                var summary = ok ? "Valid JWKS" : "Duplicates";
                var msg = ok ? $"{count} key(s), {nonNullKids.Distinct(StringComparer.Ordinal).Count()} distinct kid" : $"Duplicate kid(s): {string.Join(", ", dup)}";
                return (ok, summary, msg, count, nonNullKids.Distinct(StringComparer.Ordinal).Count(), dup);
            }
            catch (Exception ex)
            {
                return (false, "Invalid", ex.Message, 0, 0, new());
            }
        }
    }
}
