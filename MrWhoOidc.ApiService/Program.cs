using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Wire up Auth persistence to reuse the same database
builder.Services.AddAuthPersistence(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// === Admin API: Scopes ===
app.MapGet("/admin/scopes", async (AuthDbContext db, int? skip, int? take) =>
{
    IQueryable<Scope> q = db.Scopes.AsNoTracking().OrderBy(s => s.Name);
    if (skip is > 0) q = q.Skip(skip.Value);
    if (take is > 0 && take.Value <= 200) q = q.Take(take.Value);
    var list = await q.ToListAsync();
    return Results.Ok(list);
});

app.MapPost("/admin/scopes", async (AuthDbContext db, Scope input) =>
{
    input.Name = input.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "name_required" });
    var exists = await db.Scopes.AnyAsync(s => s.Name == input.Name);
    if (exists) return Results.Conflict(new { error = "scope_exists" });
    db.Scopes.Add(new Scope { Name = input.Name, Description = input.Description, IsExposed = input.IsExposed });
    await db.SaveChangesAsync();
    return Results.Created($"/admin/scopes/{input.Name}", input);
});

app.MapPut("/admin/scopes/{name}", async (AuthDbContext db, string name, Scope input) =>
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
});

app.MapDelete("/admin/scopes/{name}", async (AuthDbContext db, string name) =>
{
    var inUse = await db.ClientScopes.AnyAsync(cs => cs.ScopeName == name);
    if (inUse) return Results.Conflict(new { error = "scope_in_use" });
    var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
    if (entity is null) return Results.NotFound();
    db.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// === Admin API: Client scopes ===
app.MapGet("/admin/clients/{clientId}/scopes", async (AuthDbContext db, Guid clientId) =>
{
    var scopes = await db.ClientScopes.AsNoTracking().Where(cs => cs.ClientId == clientId).Select(cs => cs.ScopeName).OrderBy(n => n).ToListAsync();
    return Results.Ok(scopes);
});

app.MapPost("/admin/clients/{clientId}/scopes", async (AuthDbContext db, Guid clientId, string[] scopes) =>
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
});

app.MapDelete("/admin/clients/{clientId}/scopes/{scope}", async (AuthDbContext db, Guid clientId, string scope) =>
{
    var entity = await db.ClientScopes.FirstOrDefaultAsync(cs => cs.ClientId == clientId && cs.ScopeName == scope);
    if (entity is null) return Results.NotFound();
    db.ClientScopes.Remove(entity);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDefaultEndpoints();

app.Run();
