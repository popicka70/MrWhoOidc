using LicensingService.Core.Crypto;
using LicensingService.Core.Persistence;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using LicensingService.Web.Api;
using LicensingService.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Configure database provider based on configuration
var connectionString = builder.Configuration.GetConnectionString("LicensingDb");
var usePostgres = builder.Configuration.GetValue<bool>("UsePostgres");

builder.Services.AddDbContext<LicensingDbContext>(options =>
{
    if (usePostgres && !string.IsNullOrEmpty(connectionString))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        // Default to SQLite for development
        var sqliteConnection = connectionString ?? "Data Source=licensing.db";
        options.UseSqlite(sqliteConnection);
    }
});

// Register services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<IProductStore, ProductStore>();
builder.Services.AddScoped<ICustomerStore, CustomerStore>();
builder.Services.AddScoped<ISigningKeyService, SigningKeyService>();
builder.Services.AddScoped<ILicenseTokenGenerator, LicenseTokenGenerator>();
builder.Services.AddScoped<ILicenseStore, LicenseStore>();
builder.Services.AddScoped<ILicenseValidationService, LicenseValidationService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();

// Configure OIDC authentication
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = builder.Configuration["Oidc:Authority"];
        options.Audience = builder.Configuration["Oidc:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    });

builder.Services.AddAuthorization();

// Configure Health Checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<LicensingDbContext>("database", tags: ["ready"]);

// Add OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Licensing Service API", 
        Version = "v1",
        Description = "A standalone licensing service for issuing, validating, and managing software licenses. Supports license lifecycle operations including issuance, renewal, revocation, and tier changes.",
        Contact = new() { Name = "MrWhoOidc Team" }
    });
    options.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter your JWT token from the OIDC provider"
    });
    options.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
    
    // Group endpoints by tag
    options.TagActionsBy(api => new[] { api.GroupName ?? api.ActionDescriptor.RouteValues["controller"] ?? "Other" });
});

// Add Razor Pages for Admin UI
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Health check endpoints (no auth required)
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = new
        {
            Status = report.Status.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Duration = report.TotalDuration.TotalMilliseconds,
            Checks = report.Entries.Select(e => new
            {
                Name = e.Key,
                Status = e.Value.Status.ToString(),
                Duration = e.Value.Duration.TotalMilliseconds,
                Description = e.Value.Description,
                Exception = e.Value.Exception?.Message
            })
        };
        await context.Response.WriteAsJsonAsync(result);
    }
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // Liveness just checks app is running
}).AllowAnonymous();

// Map API endpoints
app.MapProductEndpoints();
app.MapCustomerEndpoints();
app.MapLicenseEndpoints();
app.MapJwksEndpoints();
app.MapValidationEndpoints();

// Map Razor Pages
app.MapRazorPages();

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
