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

// AuthN/Z for Admin API
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = false // TODO: wire to JWKS
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// Helper: Admin-only policy (placeholder)
static RouteHandlerBuilder RequireAdmin(RouteHandlerBuilder builder) => builder.RequireAuthorization();

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
        // enforce unique name on rename
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
    // prevent delete if in use by clients or assignments
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

// === Admin API: User-client assignments ===
RequireAdmin(app.MapGet("/admin/users/{userId:guid}/clients", async (AuthDbContext db, Guid userId) =>
{
    var items = await db.UserClientAssignments.AsNoTracking().Where(a => a.UserId == userId)
        .Select(a => new { a.UserId, a.ClientId, a.RealmId, a.IsActive })
        .ToListAsync();
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

// === Admin API: User-role assignments (per client+realm) ===
RequireAdmin(app.MapGet("/admin/users/{userId:guid}/roles", async (AuthDbContext db, Guid userId) =>
{
    var items = await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == userId)
        .Select(a => new { a.UserId, a.RoleId, a.ClientId, a.RealmId, a.IsActive })
        .ToListAsync();
    return Results.Ok(items);
}));

RequireAdmin(app.MapPost("/admin/users/{userId:guid}/roles", async (AuthDbContext db, Guid userId, UserRoleAssignment input) =>
{
    if (input.UserId != Guid.Empty && input.UserId != userId) return Results.BadRequest(new { error = "user_mismatch" });
    if (input.RoleId == Guid.Empty || input.ClientId == Guid.Empty || input.RealmId == Guid.Empty)
        return Results.BadRequest(new { error = "invalid_ids" });
    var exists = await db.UserRoleAssignments.AnyAsync(a => a.UserId == userId && a.RoleId == input.RoleId && a.ClientId == input.ClientId && a.RealmId == input.RealmId);
    if (!exists)
    {
        db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = userId, RoleId = input.RoleId, ClientId = input.ClientId, RealmId = input.RealmId, IsActive = input.IsActive });
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
}));

RequireAdmin(app.MapDelete("/admin/users/{userId:guid}/roles/{roleId:guid}/clients/{clientId:guid}/realms/{realmId:guid}", async (AuthDbContext db, Guid userId, Guid roleId, Guid clientId, Guid realmId) =>
{
    var entity = await db.UserRoleAssignments.FirstOrDefaultAsync(a => a.UserId == userId && a.RoleId == roleId && a.ClientId == clientId && a.RealmId == realmId);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
}));

app.MapDefaultEndpoints();

app.Run();
