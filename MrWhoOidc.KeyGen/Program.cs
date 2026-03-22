using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Api;
using MrWhoOidc.KeyGen.Configuration;
using MrWhoOidc.KeyGen.Domain.Services;
using MrWhoOidc.KeyGen.Middleware;
using MrWhoOidc.KeyGen.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Configure structured logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddEventSourceLogger();

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

    // Content Security Policy - restrictive policy for this admin app
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'");

    await next();
});

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Map API endpoints
app.MapKeyDownloadEndpoints();
app.MapLicenseDownloadEndpoints();

// Map health check endpoint
app.MapHealthChecks("/health");

app.Run();
