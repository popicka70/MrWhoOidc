using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MrWhoOidc.KeyGen.Api;
using MrWhoOidc.KeyGen.Configuration;
using MrWhoOidc.KeyGen.Domain.Services;
using MrWhoOidc.KeyGen.Middleware;
using MrWhoOidc.KeyGen.Persistence;
using MrWhoOidc.KeyGen.Security;

var builder = WebApplication.CreateBuilder(args);

// Configure structured logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddEventSourceLogger();

// Authentication fails closed: outside Development this throws unless OIDC is configured.
// Every endpoint requires the configured admin role (fallback policy) unless marked anonymous.
var authOptions = builder.AddKeyGenAuthentication();

// Add services to the container.
builder.Services.AddRazorPages();

// Configure database
var connectionString = builder.Configuration.GetConnectionString("KeyGenDb")
    ?? throw new InvalidOperationException("Connection string 'KeyGenDb' not found.");

builder.Services.AddDbContext<KeyGenDbContext>(options =>
    options.UseSqlite(connectionString));

// Configure options
builder.Services.Configure<KeyGenOptions>(
    builder.Configuration.GetSection(KeyGenOptions.SectionName));

// Register domain services
builder.Services.AddScoped<IKeyGenerationService, KeyGenerationService>();
builder.Services.AddScoped<ILicenseGenerationService, LicenseGenerationService>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<KeyGenDbContext>();

// Configure antiforgery
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

var app = builder.Build();

if (authOptions.DisableInDevelopment)
{
    app.Logger.LogWarning(
        "KeyGen authentication is DISABLED (KeyGen:Auth:DisableInDevelopment=true). Every request runs as '{User}'. Development only.",
        DevelopmentAuthenticationHandler.DisplayName);
}

// Apply migrations automatically on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<KeyGenDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.

// Add correlation ID middleware first
app.UseMiddleware<CorrelationIdMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Add security headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

    // Content Security Policy - restrictive policy for this admin app.
    // NOTE: 'unsafe-inline' is currently required because the Razor views use inline
    // <script> blocks, inline onclick handlers, and inline style attributes. Removing it
    // requires migrating those to external files plus a per-request nonce. Tracked as a
    // hardening follow-up in docs/oidc-idp-assessment-2026-10-04.md (KeyGen CSP).
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'");

    // Pages and API responses can carry license JWTs and private JWKs: never cache them.
    // Responses that set their own Cache-Control (static assets, antiforgery) are left alone.
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        if (!headers.ContainsKey(HeaderNames.CacheControl))
        {
            headers.CacheControl = "no-store";
            headers.Pragma = "no-cache";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Static assets (CSS/JS) are public so the AccessDenied and Error pages render.
app.MapStaticAssets()
   .AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();

// Map API endpoints
app.MapKeyDownloadEndpoints();
app.MapLicenseDownloadEndpoints();

// Map health check endpoint
app.MapHealthChecks("/health")
   .AllowAnonymous();

app.Run();
