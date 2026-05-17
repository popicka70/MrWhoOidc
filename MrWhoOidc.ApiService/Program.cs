using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Isopoh.Cryptography.Argon2;
using System.Text;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Wire up Auth persistence to reuse the same database
builder.Services.AddAuthPersistence(builder.Configuration);

// Admin auth options (issuer/JWKS + realm/role)
var adminAuth = builder.Configuration.GetSection("AdminAuth").Get<AdminAuthOptions>() ?? new AdminAuthOptions();
var allowDevelopmentJwtFallback = builder.Environment.IsDevelopment();

// AuthN/Z for Admin API
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        if (!string.IsNullOrWhiteSpace(adminAuth.Issuer)) {
            options.Authority = adminAuth.Issuer;
            options.RequireHttpsMetadata = adminAuth.RequireHttpsMetadata;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = adminAuth.Issuer,
                ValidateAudience = false,
                ValidateLifetime = true,
                NameClaimType = "sub",
                RoleClaimType = "roles"
            };
        }
        else {
            if (!allowDevelopmentJwtFallback)
            {
                throw new InvalidOperationException("AdminAuth:Issuer must be configured outside Development.");
            }

            // Fallback (dev): minimal validation
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                NameClaimType = "sub",
                RoleClaimType = "roles"
            };
        }
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireAssertion(ctx =>
    {
        var realm = ctx.User.FindFirst("realm")?.Value;
        if (!string.Equals(realm, adminAuth.RealmName, StringComparison.OrdinalIgnoreCase)) return false;
        var roles = ctx.User.FindAll("roles").Select(c => c.Value);
        return roles.Any(r => string.Equals(r, adminAuth.AdminRoleName, StringComparison.OrdinalIgnoreCase));
    }));

    // Simple API policy for audience 'api'
    options.AddPolicy("api", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// Helper: Admin-only policy
static RouteHandlerBuilder RequireAdmin(RouteHandlerBuilder builder) => builder.RequireAuthorization("admin");

// === Admin API: Scopes ===
RequireAdmin(app.MapGet("/admin/scopes", async (AuthDbContext db, int? skip, int? take) =>
{
    IQueryable<Scope> q = db.Scopes.AsNoTracking().OrderBy(s => s.Name);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var list = await q.ToListAsync();
    return Results.Ok(list);
}));

RequireAdmin(app.MapPost("/admin/scopes", async (AuthDbContext db, Scope input) =>
{
    input.Name = input.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
    var exists = await db.Scopes.AnyAsync(s => s.Name == input.Name);
    if (exists) return Results.Conflict(new { error = "scope_exists" });
    db.Scopes.Add(new Scope { Name = input.Name, Description = input.Description, IsExposed = input.IsExposed });
    await db.SaveChangesAsync();
    return Results.Created($"/admin/scopes/{input.Name}", input);
}));

RequireAdmin(app.MapPut("/admin/scopes/{name}", async (AuthDbContext db, string name, Scope input) =>
{
    var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
    if (entity is null) return Results.NotFound();
    if (!string.Equals(name, input.Name, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "name_immutable" });
    }
    entity.Description = input.Description;
    entity.IsExposed = input.IsExposed;
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/scopes/{name}", async (AuthDbContext db, string name) =>
{
    var inUse = await db.ClientScopes.AnyAsync(cs => cs.ScopeName == name);
    if (inUse) return Results.Conflict(new { error = "scope_in_use" });
    var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: Client scopes ===
RequireAdmin(app.MapGet("/admin/clients/{clientId}/scopes", async (AuthDbContext db, Guid clientId) =>
{
    var scopes = await db.ClientScopes.AsNoTracking().Where(cs => cs.ClientId == clientId).Select(cs => cs.ScopeName).OrderBy(n => n).ToListAsync();
    return Results.Ok(scopes);
}));

RequireAdmin(app.MapPost("/admin/clients/{clientId}/scopes", async (AuthDbContext db, Guid clientId, string[] scopes) =>
{
    var existing = await db.ClientScopes.Where(cs => cs.ClientId == clientId).Select(cs => cs.ScopeName).ToListAsync();
    var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);
    var toAdd = scopes.Distinct(StringComparer.Ordinal);
    foreach (var s in toAdd)
    {
        if (existingSet.Contains(s)) continue;
        if (!await db.Scopes.AnyAsync(x => x.Name == s)) return Results.BadRequest(new { error = "unknown_scope", scope = s });
        db.ClientScopes.Add(new ClientScope { ClientId = clientId, ScopeName = s });
    }
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/clients/{clientId}/scopes/{scope}", async (AuthDbContext db, Guid clientId, string scope) =>
{
    var entity = await db.ClientScopes.FirstOrDefaultAsync(cs => cs.ClientId == clientId && cs.ScopeName == scope);
    if (entity is null) return Results.NotFound();
    db.ClientScopes.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: Clients (basic CRUD) ===
RequireAdmin(app.MapGet("/admin/clients", async (AuthDbContext db, string? search, int? skip, int? take) =>
{
    var q = db.Clients.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var s = search.Trim();
        q = q.Where(c => c.ClientId.Contains(s) || (c.ClientName != null && c.ClientName.Contains(s)));
    }
    q = q.OrderBy(c => c.ClientId);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var list = await q.ToListAsync();
    return Results.Ok(list);
}));

RequireAdmin(app.MapPost("/admin/clients", async (AuthDbContext db, Client input) =>
{
    var clientId = (input.ClientId ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(clientId)) return Results.BadRequest(new { error = "client_id_required" });
    var exists = await db.Clients.AnyAsync(c => c.ClientId == clientId);
    if (exists) return Results.Conflict(new { error = "client_id_exists" });

var entity = new Client
    {
        ClientId = clientId,
        ClientName = input.ClientName,
        RequirePkce = input.RequirePkce,
        RequireConsent = input.RequireConsent,
        RealmId = input.RealmId,
        PublicJwksJson = input.PublicJwksJson,
        PublicJwksUri = input.PublicJwksUri,
        RequirePar = input.RequirePar
    };

// If a raw secret is provided, hash with Argon2 and add to ClientSecrets collection
#pragma warning disable CS0618 // ClientSecretHash is obsolete but needed for backward compatibility during migration
var secret = (input.ClientSecretHash ?? string.Empty).Trim();
#pragma warning restore CS0618
if (!string.IsNullOrEmpty(secret))
{
    var config = new Argon2Config
    {
        Type = Argon2Type.HybridAddressing,
        Version = Argon2Version.Nineteen,
        TimeCost = 4,
        MemoryCost = 131072,
        Lanes = 4,
        Threads = 1,
        Password = Encoding.UTF8.GetBytes(secret),
        Salt = RandomNumberGenerator.GetBytes(16),
        HashLength = 32
    };

    using var argon2 = new Argon2(config);
    using var hash = argon2.Hash();
    var secretHash = $"v2:{config.EncodeString(hash.Buffer)}";
    
    entity.ClientSecrets.Add(new ClientSecret
    {
        SecretHash = secretHash,
        CreatedAtUtc = DateTime.UtcNow,
        ActivatedAtUtc = DateTime.UtcNow,
        IsPrimary = true,
        CreatedBy = "API"
    });
}

    db.Clients.Add(entity);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/clients/{entity.Id}", entity);
}));

RequireAdmin(app.MapPut("/admin/clients/{id:guid}", async (AuthDbContext db, Guid id, Client input) =>
{
    var entity = await db.Clients.FirstOrDefaultAsync(c => c.Id == id);
    if (entity is null) return Results.NotFound();

    var newClientId = (input.ClientId ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(newClientId)) return Results.BadRequest(new { error = "client_id_required" });
    if (!string.Equals(entity.ClientId, newClientId, StringComparison.Ordinal))
    {
        var exists = await db.Clients.AnyAsync(c => c.ClientId == newClientId);
        if (exists) return Results.Conflict(new { error = "client_id_exists" });
        entity.ClientId = newClientId;
    }

    entity.ClientName = input.ClientName;
    entity.RequirePkce = input.RequirePkce;
    entity.RequireConsent = input.RequireConsent;
    entity.RealmId = input.RealmId;
    entity.PublicJwksJson = input.PublicJwksJson;
    entity.PublicJwksUri = input.PublicJwksUri;
    entity.RequirePar = input.RequirePar;

// If a new raw secret is provided, hash and replace
#pragma warning disable CS0618 // ClientSecretHash is obsolete but needed for backward compatibility during migration
var secret = (input.ClientSecretHash ?? string.Empty).Trim();
#pragma warning restore CS0618
if (!string.IsNullOrEmpty(secret))
{
    var config = new Argon2Config
    {
        Type = Argon2Type.HybridAddressing,
        Version = Argon2Version.Nineteen,
        TimeCost = 4,
        MemoryCost = 131072,
        Lanes = 4,
        Threads = 1,
        Password = Encoding.UTF8.GetBytes(secret),
        Salt = RandomNumberGenerator.GetBytes(16),
        HashLength = 32
    };

    using var argon2 = new Argon2(config);
    using var hash = argon2.Hash();
    var secretHash = $"v2:{config.EncodeString(hash.Buffer)}";
    
    // Deactivate existing primary secrets and add new one as primary
    foreach (var existingSecret in entity.ClientSecrets.Where(s => s.IsPrimary && s.RevokedAtUtc == null))
    {
        existingSecret.RevokedAtUtc = DateTime.UtcNow;
        existingSecret.RevokedBy = "API";
    }
    
    entity.ClientSecrets.Add(new ClientSecret
    {
        SecretHash = secretHash,
        CreatedAtUtc = DateTime.UtcNow,
        ActivatedAtUtc = DateTime.UtcNow,
        IsPrimary = true,
        CreatedBy = "API"
    });
}

    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/clients/{id:guid}", async (AuthDbContext db, Guid id) =>
{
    // Prevent delete when in use
    var inUse = await db.AuthorizationCodes.AnyAsync(c => c.ClientId == id.ToString())
        || await db.Consents.AnyAsync(c => c.ClientId == id.ToString())
        || await db.Tokens.AnyAsync(t => t.ClientId == id.ToString())
        || await db.UserClientAssignments.AnyAsync(a => a.ClientId == id)
        || await db.UserClientRoleAssignments.AnyAsync(a => a.ClientId == id);
    if (inUse) return Results.Conflict(new { error = "client_in_use" });

    var entity = await db.Clients.FirstOrDefaultAsync(c => c.Id == id);
    if (entity is null) return Results.NotFound();
    db.Clients.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: Users (basic CRUD) ===
RequireAdmin(app.MapGet("/admin/users", async (AuthDbContext db, string? search, int? skip, int? take) =>
{
    var q = db.Users.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var s = search.Trim();
        q = q.Where(u => u.Username.Contains(s) || (u.Email != null && u.Email.Contains(s)) || (u.Name != null && u.Name.Contains(s)));
    }
    q = q.OrderBy(u => u.Username);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var list = await q.Select(u => new { u.Id, u.Username, u.Email, u.EmailVerified, u.Name, u.CreatedAt }).ToListAsync();
    return Results.Ok(list);
}));

RequireAdmin(app.MapGet("/admin/users/{id:guid}", async (AuthDbContext db, Guid id) =>
{
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    if (user is null) return Results.NotFound();
    return Results.Ok(new { user.Id, user.Username, user.Email, user.EmailVerified, user.Name, user.CreatedAt });
}));

RequireAdmin(app.MapPost("/admin/users", async (AuthDbContext db, User input) =>
{
    var username = (input.Username ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(username)) return Results.BadRequest(new { error = "username_required" });
    var exists = await db.Users.AnyAsync(u => u.Username == username);
    if (exists) return Results.Conflict(new { error = "username_exists" });
    var email = input.Email?.Trim().ToLowerInvariant();
    if (!string.IsNullOrEmpty(email) && await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { error = "email_exists" });
    var user = new User
    {
        Username = username,
        Name = input.Name,
        Email = email,
        EmailVerified = false
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/users/{user.Id}", new { user.Id, user.Username, user.Email, user.EmailVerified, user.Name, user.CreatedAt });
}));

RequireAdmin(app.MapPut("/admin/users/{id:guid}", async (AuthDbContext db, Guid id, User input) =>
{
    var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (entity is null) return Results.NotFound();
    var newUsername = (input.Username ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(newUsername)) return Results.BadRequest(new { error = "username_required" });
    if (!string.Equals(entity.Username, newUsername, StringComparison.Ordinal))
    {
        var exists = await db.Users.AnyAsync(u => u.Username == newUsername);
        if (exists) return Results.Conflict(new { error = "username_exists" });
        entity.Username = newUsername;
    }
    var newEmail = input.Email?.Trim().ToLowerInvariant();
    if (!string.Equals(entity.Email, newEmail, StringComparison.Ordinal))
    {
        if (!string.IsNullOrEmpty(newEmail) && await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != id))
            return Results.Conflict(new { error = "email_exists" });
        entity.Email = newEmail;
        entity.EmailVerified = false;
        entity.EmailVerifiedAt = null;
    }
    entity.Name = input.Name;
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/users/{id:guid}", async (AuthDbContext db, Guid id) =>
{
    var inUse = await db.Tokens.AnyAsync(t => t.UserId == id)
        || await db.Consents.AnyAsync(c => c.UserId == id)
        || await db.UserClientAssignments.AnyAsync(a => a.UserId == id)
        || await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == id)
        || await db.UserClientRoleAssignments.AnyAsync(a => a.UserId == id);
    if (inUse) return Results.Conflict(new { error = "user_in_use" });
    var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: Alternative emails ===
RequireAdmin(app.MapGet("/admin/users/{userId:guid}/emails", async (AuthDbContext db, Guid userId) =>
{
    var items = await db.UserAlternativeEmails.AsNoTracking().Where(a => a.UserId == userId)
        .Select(a => new { a.Id, a.Email, a.IsVerified, a.VerifiedAt })
        .ToListAsync();
    return Results.Ok(items);
}));

RequireAdmin(app.MapPost("/admin/users/{userId:guid}/emails", async (AuthDbContext db, Guid userId, UserAlternativeEmail input) =>
{
    var email = (input.Email ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { error = "email_required" });
    var exists = await db.UserAlternativeEmails.AnyAsync(a => a.UserId == userId && a.Email == email);
    if (exists) return Results.Conflict(new { error = "email_exists" });
    db.UserAlternativeEmails.Add(new UserAlternativeEmail { UserId = userId, Email = email, IsVerified = false });
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapPut("/admin/users/{userId:guid}/emails/{emailId:guid}", async (AuthDbContext db, Guid userId, Guid emailId, UserAlternativeEmail input) =>
{
    var entity = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == emailId && a.UserId == userId);
    if (entity is null) return Results.NotFound();
    entity.IsVerified = input.IsVerified;
    entity.VerifiedAt = input.IsVerified ? (entity.VerifiedAt ?? DateTimeOffset.UtcNow) : null;
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/users/{userId:guid}/emails/{emailId:guid}", async (AuthDbContext db, Guid userId, Guid emailId) =>
{
    var entity = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == emailId && a.UserId == userId);
    if (entity is null) return Results.NotFound();
    db.UserAlternativeEmails.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: User-client assignments ===
RequireAdmin(app.MapGet("/admin/users/{userId:guid}/clients", async (AuthDbContext db, Guid userId, int? skip, int? take) =>
{
    IQueryable<UserClientAssignment> q = db.UserClientAssignments.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.ClientId);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var items = await q.Select(a => new { a.UserId, a.ClientId, a.RealmId, a.IsActive }).ToListAsync();
    return Results.Ok(items);
}));

RequireAdmin(app.MapPost("/admin/users/{userId:guid}/clients", async (AuthDbContext db, Guid userId, UserClientAssignment input) =>
{
    if (input.UserId != Guid.Empty && input.UserId != userId) return Results.BadRequest(new { error = "user_mismatch" });
    if (input.ClientId == Guid.Empty || input.RealmId == Guid.Empty) return Results.BadRequest(new { error = "invalid_ids" });
    var exists = await db.UserClientAssignments.AnyAsync(a => a.UserId == userId && a.ClientId == input.ClientId && a.RealmId == input.RealmId);
    if (!exists)
    {
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = userId, ClientId = input.ClientId, RealmId = input.RealmId, IsActive = input.IsActive });
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/users/{userId:guid}/clients/{clientId:guid}/realms/{realmId:guid}", async (AuthDbContext db, Guid userId, Guid clientId, Guid realmId) =>
{
    var entity = await db.UserClientAssignments.FirstOrDefaultAsync(a => a.UserId == userId && a.ClientId == clientId && a.RealmId == realmId);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: User-role assignments (per client) ===
RequireAdmin(app.MapGet("/admin/users/{userId:guid}/roles", async (AuthDbContext db, Guid userId, int? skip, int? take) =>
{
    IQueryable<UserClientRoleAssignment> q = db.UserClientRoleAssignments.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.ClientId);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var items = await q.Select(a => new { a.UserId, a.RoleId, a.ClientId, a.IsActive }).ToListAsync();
    return Results.Ok(items);
}));

RequireAdmin(app.MapPost("/admin/users/{userId:guid}/roles", async (AuthDbContext db, Guid userId, UserClientRoleAssignment input) =>
{
    if (input.UserId != Guid.Empty && input.UserId != userId) return Results.BadRequest(new { error = "user_mismatch" });
    if (input.RoleId == Guid.Empty || input.ClientId == Guid.Empty)
        return Results.BadRequest(new { error = "invalid_ids" });
    var exists = await db.UserClientRoleAssignments.AnyAsync(a => a.UserId == userId && a.RoleId == input.RoleId && a.ClientId == input.ClientId);
    if (!exists)
    {
        db.UserClientRoleAssignments.Add(new UserClientRoleAssignment { UserId = userId, RoleId = input.RoleId, ClientId = input.ClientId, IsActive = input.IsActive });
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/users/{userId:guid}/roles/{roleId:guid}/clients/{clientId:guid}", async (AuthDbContext db, Guid userId, Guid roleId, Guid clientId) =>
{
    var entity = await db.UserClientRoleAssignments.FirstOrDefaultAsync(a => a.UserId == userId && a.RoleId == roleId && a.ClientId == clientId);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

app.MapDefaultEndpoints();

// --- Test protected API endpoint for M2M proof of concept ---
app.MapGet("/test/protected", (System.Security.Claims.ClaimsPrincipal user) =>
{
    // return something simple; include sub/client_id if present
    var sub = user.FindFirst("sub")?.Value ?? "(no sub)";
    var clientId = user.FindFirst("client_id")?.Value ?? "(no client_id)";
    return Results.Ok(new { message = "OK from protected API", sub, client_id = clientId, when = DateTimeOffset.UtcNow });
}).RequireAuthorization("api");

// --- RFC 9470: Step-Up Authentication Challenge ---
// This endpoint requires a specific ACR level (e.g. urn:mfa). If the bearer token's
// acr claim doesn't match, it returns 401 with the WWW-Authenticate step-up challenge
// so a smart client can re-drive the authorize flow with the correct acr_values/max_age.
app.MapGet("/test/step-up-required", (System.Security.Claims.ClaimsPrincipal user, HttpContext ctx) =>
{
    const string RequiredAcr = "urn:mfa";
    const int RequiredMaxAgeSec = 900; // 15 min

    // Must be authenticated first (bearer token present and valid)
    if (!user.Identity?.IsAuthenticated ?? true)
    {
        ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"api\"";
        return Results.Unauthorized();
    }

    var acr = user.FindFirst("acr")?.Value;
    var authTimeRaw = user.FindFirst("auth_time")?.Value;
    DateTimeOffset? authTime = long.TryParse(authTimeRaw, out var epochSec)
        ? DateTimeOffset.FromUnixTimeSeconds(epochSec)
        : null;
    var ageSec = authTime.HasValue ? (int)(DateTimeOffset.UtcNow - authTime.Value).TotalSeconds : int.MaxValue;

    if (!string.Equals(acr, RequiredAcr, StringComparison.Ordinal) || ageSec > RequiredMaxAgeSec)
    {
        // RFC 9470 §3: resource server issues a challenge with the requirements the client must satisfy
        ctx.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_user_authentication\"," +
            $" acr_values=\"{RequiredAcr}\"," +
            $" max_age={RequiredMaxAgeSec}";
        return Results.Unauthorized();
    }

    var sub = user.FindFirst("sub")?.Value ?? "(no sub)";
    return Results.Ok(new { message = "High-assurance resource accessed", sub, acr, auth_time = authTimeRaw, when = DateTimeOffset.UtcNow });
}).RequireAuthorization("api");

app.Run();

public sealed class AdminAuthOptions
{
    public string? Issuer { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true; // Secure by default
    public string RealmName { get; set; } = "admin";
    public string AdminRoleName { get; set; } = "admin";
}
