using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

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

// AuthN/Z for Admin API
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (!string.IsNullOrWhiteSpace(adminAuth.Issuer))
        {
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
        else
        {
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
    var toAdd = scopes.Distinct(StringComparer.Ordinal).Except(existing, StringComparer.Ordinal).ToArray();
    foreach (var s in toAdd)
    {
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

// === Admin API: Realms ===
RequireAdmin(app.MapGet("/admin/realms", async (AuthDbContext db) =>
    Results.Ok(await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync())));

RequireAdmin(app.MapPost("/admin/realms", async (AuthDbContext db, Realm input) =>
{
    input.Name = input.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
    var exists = await db.Realms.AnyAsync(r => r.Name == input.Name);
    if (exists) return Results.Conflict(new { error = "realm_exists" });
    var realm = new Realm { Name = input.Name, DisplayName = input.DisplayName };
    db.Realms.Add(realm);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/realms/{realm.Id}", realm);
}));

RequireAdmin(app.MapPut("/admin/realms/{id:guid}", async (AuthDbContext db, Guid id, Realm input) =>
{
    var entity = await db.Realms.FirstOrDefaultAsync(r => r.Id == id);
    if (entity is null) return Results.NotFound();
    if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
        var exists = await db.Realms.AnyAsync(r => r.Name == input.Name && r.Id != id);
        if (exists) return Results.Conflict(new { error = "realm_exists" });
        entity.Name = input.Name;
    }
    entity.DisplayName = input.DisplayName;
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/realms/{id:guid}", async (AuthDbContext db, Guid id) =>
{
    var used = await db.Clients.AnyAsync(c => c.RealmId == id)
        || await db.UserClientAssignments.AnyAsync(a => a.RealmId == id)
        || await db.UserRoleAssignments.AnyAsync(a => a.RealmId == id)
        || await db.Roles.AnyAsync(r => r.RealmId == id);
    if (used) return Results.Conflict(new { error = "realm_in_use" });
    var entity = await db.Realms.FirstOrDefaultAsync(r => r.Id == id);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

// === Admin API: Roles (per realm) ===
RequireAdmin(app.MapGet("/admin/realms/{realmId:guid}/roles", async (AuthDbContext db, Guid realmId) =>
{
    var items = await db.Roles.AsNoTracking().Where(r => r.RealmId == realmId).OrderBy(r => r.Name).ToListAsync();
    return Results.Ok(items);
}));

RequireAdmin(app.MapPost("/admin/realms/{realmId:guid}/roles", async (AuthDbContext db, Guid realmId, Role input) =>
{
    input.Name = input.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
    var exists = await db.Roles.AnyAsync(r => r.RealmId == realmId && r.Name == input.Name);
    if (exists) return Results.Conflict(new { error = "role_exists" });
    var role = new Role { Name = input.Name, RealmId = realmId, IsActive = input.IsActive };
    db.Roles.Add(role);
    await db.SaveChangesAsync();
    return Results.Created($"/admin/realms/{realmId}/roles/{role.Id}", role);
}));

RequireAdmin(app.MapPut("/admin/realms/{realmId:guid}/roles/{id:guid}", async (AuthDbContext db, Guid realmId, Guid id, Role input) =>
{
    var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.RealmId == realmId);
    if (entity is null) return Results.NotFound();
    if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
        var exists = await db.Roles.AnyAsync(r => r.RealmId == realmId && r.Name == input.Name && r.Id != id);
        if (exists) return Results.Conflict(new { error = "role_exists" });
        entity.Name = input.Name;
    }
    entity.IsActive = input.IsActive;
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/realms/{realmId:guid}/roles/{id:guid}", async (AuthDbContext db, Guid realmId, Guid id) =>
{
    var used = await db.UserRoleAssignments.AnyAsync(a => a.RoleId == id);
    if (used) return Results.Conflict(new { error = "role_in_use" });
    var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.RealmId == realmId);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
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
        EmailVerified = false,
        HashAlgorithm = "argon2id",
        PasswordHash = string.Empty
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
        || await db.UserRoleAssignments.AnyAsync(a => a.UserId == id);
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
    // Allow toggling verification status
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

app.MapDefaultEndpoints();

app.Run();

public sealed class AdminAuthOptions
{
    public string? Issuer { get; set; }
    public bool RequireHttpsMetadata { get; set; } = false; // dev default
    public string RealmName { get; set; } = "admin";
    public string AdminRoleName { get; set; } = "admin";
}
