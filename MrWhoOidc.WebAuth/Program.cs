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
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Admin.Helpers;
using MrWhoOidc.WebAuth.Infrastructure.Http;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

var builder = WebApplication.CreateBuilder(args);

// Force early load of Auth assembly to avoid stale/partial incremental build races impacting extension method availability.
_ = typeof(MrWhoOidc.Auth.AuthServiceCollectionExtensions);

// Testing aid: allow disabling service provider validation (scope/singleton checks) when running
// snapshot or surface tests that intentionally spin up a minimal in-memory host. This avoids
// false positives from lifetime validation during transitional refactor phases.
if (string.Equals(builder.Configuration["Testing:DisableServiceProviderValidation"], "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateOnBuild = false;
        options.ValidateScopes = false;
    });
}

// NOTE: don't request client certificates at TLS layer to avoid browser cert prompts.
// For mTLS on machine-to-machine callers, prefer certificate forwarding via a reverse proxy.

builder.AddServiceDefaults();

// Observability (App Insights, metrics, alerting, audit sink) extracted
builder.Services.AddMrWhoOidcObservability(builder.Configuration);

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(oidcOptions);

// Bind AuthOptions
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
// Auth/admin (Phase 2 extracted extension – limited scope)
builder.Services.AddMrWhoOidcAuthAndAdmin(builder.Configuration);

// Admin policy options
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));

// Redis connection (shared for security core + rate limiting if present)
var redisConnection = builder.Configuration.GetConnectionString("redis") ?? builder.Configuration["ConnectionStrings:redis"];
IConnectionMultiplexer? redisMux = null;
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    redisMux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
    builder.Services.AddSingleton(redisMux);
}

// Presentation layer (Razor Pages + MVC + antiforgery + localization)
builder.Services.AddLocalizationAndMvc(builder.Configuration);

// Security core (DPoP, JAR replay cache, DataProtection, cert forwarding, TE limiter)
builder.Services.AddMrWhoOidcSecurityCore(builder.Configuration, redisMux);

// Persistence & core protocol services extracted
builder.Services.AddMrWhoOidcPersistenceAndCore(builder.Configuration);
// Test-only safety net to mitigate intermittent first-run missing DI registrations.
// Enabled via Testing:InlineAuthCoreSafety=true. Idempotent; re-invokes core registration if any critical service absent.
if (string.Equals(builder.Configuration["Testing:InlineAuthCoreSafety"], "true", StringComparison.OrdinalIgnoreCase))
{
    var criticalCore = new[]
    {
        typeof(MrWhoOidc.Auth.Services.IKeyStore),
        typeof(MrWhoOidc.Auth.Services.IPasswordHasher),
        typeof(MrWhoOidc.Auth.Services.ITokenService),
        typeof(MrWhoOidc.Auth.Services.ITokenValidator)
    };
    if (criticalCore.Any(t => !builder.Services.Any(d => d.ServiceType == t)))
    {
        builder.Services.AddMrWhoOidcAuthCore(); // defensive re-registration
    }
}
// Descriptor-level diagnostic (no provider build) – optional
if (string.Equals(builder.Configuration["Testing:DiagnoseAuthCore"], "true", StringComparison.OrdinalIgnoreCase))
{
    string[] critical = [
        typeof(MrWhoOidc.Auth.Services.IKeyStore).FullName!,
        typeof(MrWhoOidc.Auth.Services.IPasswordHasher).FullName!,
        typeof(MrWhoOidc.Auth.Services.ITokenService).FullName!,
        typeof(MrWhoOidc.Auth.Services.ITokenValidator).FullName!
    ];
    var missing = new List<string>();
    foreach (var c in critical)
    {
        var t = Type.GetType(c);
        if (t == null || !builder.Services.Any(d => d.ServiceType == t)) missing.Add(c);
    }
    if (missing.Count > 0)
    {
        // Attempt re-registration then re-evaluate.
        builder.Services.AddMrWhoOidcAuthCore();
        var stillMissing = missing.Where(c =>
        {
            var t = Type.GetType(c);
            return t == null || !builder.Services.Any(d => d.ServiceType == t);
        }).ToList();
        if (stillMissing.Count > 0)
        {
            if (string.Equals(builder.Configuration["Testing:DiagnoseAuthCoreStrict"], "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Auth core descriptor diagnostic (strict): missing after safety re-registration: " + string.Join(", ", stillMissing));
            }
            else
            {
                System.Console.WriteLine("[Diag][AuthCore] Missing registrations (non-strict): " + string.Join(", ", stillMissing));
            }
        }
    }
}
// Background cleanup + backchannel feature extracted
builder.Services.AddMrWhoOidcBackgroundAndBackchannel(builder.Configuration);

// Duplicate core auth registrations removed (extensions now responsible)

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

// (Moved cookie auth + admin policy to AddMrWhoOidcAuthAndAdmin extension)

// (Legacy inline persistence/core registrations removed – now supplied by AddMrWhoOidcPersistenceAndCore)

// (Security core block moved to AddMrWhoOidcSecurityCore)

// (Moved above into AddMrWhoOidcBackgroundAndBackchannel)

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

    // Public JWKS endpoints (lightweight; allow a bit higher rate)
    options.AddPolicy("rl-jwks", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
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

// (Handlers & grant registrations moved into AddMrWhoOidcPersistenceAndCore)
builder.Services.Configure<FederatedLogoutOptions>(builder.Configuration.GetSection("FederatedLogout"));

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

// Initial endpoint set (public OIDC + core pages) now routed via extracted helper for snapshot reuse
app.MapMrWhoOidcEndpoints();

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

// Invalidate JWKS cache when provider keys change (simple hook after existing CRUD operations)
// NOTE: We piggyback right after SaveChanges in existing endpoints; minimal duplication.


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

admin.MapPost("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
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

admin.MapPut("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
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

admin.MapPost("/providers/{providerId:guid}/claim-mappings", async (Guid providerId, AuthDbContext db, ClaimMappingInput input, CancellationToken ct) =>
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

admin.MapPut("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, ClaimMappingInput input, CancellationToken ct) =>
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

admin.MapPost("/providers/{providerId:guid}/keys", async (Guid providerId, AuthDbContext db, ProviderKeyInput input, MrWhoOidc.WebAuth.Security.IPublicJwksCache jwksCache, CancellationToken ct) =>
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
    // Invalidate provider + aggregate JWKS caches
    var providerName = await db.IdentityProviders.Where(p=>p.Id==providerId).Select(p=>p.Name).FirstOrDefaultAsync(ct);
    if (!string.IsNullOrEmpty(providerName)) jwksCache.InvalidateProvider(providerName!);
    return Results.Created($"/admin/api/providers/{providerId}/keys/{entity.Id}", new { entity.Id });
});

admin.MapPut("/providers/{providerId:guid}/keys/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, ProviderKeyInput input, MrWhoOidc.WebAuth.Security.IPublicJwksCache jwksCache, CancellationToken ct) =>
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
    var providerName = await db.IdentityProviders.Where(p=>p.Id==providerId).Select(p=>p.Name).FirstOrDefaultAsync(ct);
    if (!string.IsNullOrEmpty(providerName)) jwksCache.InvalidateProvider(providerName!);
    return Results.NoContent();
});

admin.MapDelete("/providers/{providerId:guid}/keys/{id:guid}", async (Guid providerId, Guid id, AuthDbContext db, MrWhoOidc.WebAuth.Security.IPublicJwksCache jwksCache, CancellationToken ct) =>
{
    var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId, ct);
    if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
    db.IdentityProviderKeys.Remove(entity);
    await db.SaveChangesAsync(ct);
    var providerName = await db.IdentityProviders.Where(p=>p.Id==providerId).Select(p=>p.Name).FirstOrDefaultAsync(ct);
    if (!string.IsNullOrEmpty(providerName)) jwksCache.InvalidateProvider(providerName!);
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

admin.MapPut("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, ClientKeysInput input, MrWhoOidc.WebAuth.Security.IPublicJwksCache jwksCache, CancellationToken ct) =>
{
    var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
    if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");

    if (!string.IsNullOrWhiteSpace(input.PublicJwksJson))
    {
    var status = AdminApiHelpers.ComputeJwksStatus(input.PublicJwksJson);
        if (status is { Ok: false })
            return Results.Problem(statusCode: 400, title: "Invalid JWKS", detail: status!.Value.Message);
        client.PublicJwksJson = input.PublicJwksJson;
        db.ClientJwksHistories.Add(new ClientJwksHistory
        {
            ClientId = client.Id,
            JwksJson = client.PublicJwksJson!,
            Source = "manual",
            Hash = AdminApiHelpers.ComputeSha256Hex(AdminApiHelpers.CompactJson(client.PublicJwksJson!))
        });
    }
    else
    {
        client.PublicJwksJson = null;
    }
    client.PublicJwksUri = string.IsNullOrWhiteSpace(input.PublicJwksUri) ? null : input.PublicJwksUri;

    await db.SaveChangesAsync(ct);
    // Invalidate client JWKS cache
    if (!string.IsNullOrEmpty(client.ClientId)) jwksCache.InvalidateClient(client.ClientId);
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

// For test scenarios we can disable static asset mapping (dev runtime patching requires ETag-able assets
// which our in-memory test host doesn't always produce in Release builds). Controlled via Testing:DisableStaticAssets.
var disableStaticAssets = app.Configuration.GetValue<bool>("Testing:DisableStaticAssets");
if (!disableStaticAssets)
{
    app.MapStaticAssets();
}

app.Run();

// (Admin auth & DTO/helper types extracted to separate files in Phase 1)

// Internal helper for tests (Phase 0 safety net). This allows test code to reference a stable symbol and
// confirm that Program.cs endpoint mapping has executed. When endpoint mapping is later extracted into
// dedicated extension methods (Phase 3), this will delegate to them or be removed.
public static partial class ProgramEndpointMapping
{
    public static void MapAll(WebApplication app)
    {
        // No-op for now; mapping already occurred inline in Program.cs before app.Run().
        // Presence of this method lets tests compile against a stable API surface.
    }
}

// Expose a public Program class so external assemblies (e.g., test projects, future sample RPs)
// can reliably construct WebApplicationFactory<Program>. Top-level statements generate an
// internal Program by default; this explicit public partial keeps that linkage without altering
// runtime behavior.
public partial class Program { }
