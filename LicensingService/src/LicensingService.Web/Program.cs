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

// Configure OIDC authentication
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = builder.Configuration["Oidc:Authority"];
        options.Audience = builder.Configuration["Oidc:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    });

builder.Services.AddAuthorization();

// Add OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Licensing Service API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter your JWT token"
    });
    options.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
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

// Health check endpoint (no auth required)
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTimeOffset.UtcNow }))
    .WithName("HealthCheck")
    .WithTags("Health")
    .AllowAnonymous();

// Map API endpoints
app.MapProductEndpoints();
app.MapCustomerEndpoints();
app.MapLicenseEndpoints();
app.MapJwksEndpoints();

// Map Razor Pages
app.MapRazorPages();

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
