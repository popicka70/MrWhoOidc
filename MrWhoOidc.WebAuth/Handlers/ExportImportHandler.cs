using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// API handlers for configuration export and import operations.
/// </summary>
public static class ExportImportHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Exports a tenant's complete configuration.
    /// </summary>
    /// <remarks>
    /// GET /admin/api/platform/tenants/{slug}/export?mode=obfuscated
    /// </remarks>
    [Authorize(Policy = "PlatformAdmin")]
    public static async Task<IResult> ExportTenant(
        [FromRoute] string slug,
        [FromQuery] string? mode,
        [FromServices] AuthDbContext dbContext,
        [FromServices] IConfigurationExportService exportService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Find tenant by slug
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found", slug });
        }

        // Parse export mode
        var exportMode = mode?.ToLowerInvariant() switch
        {
            "full" => ExportMode.Full,
            _ => ExportMode.Obfuscated
        };

        var options = new ExportOptions
        {
            Mode = exportMode,
            IncludeMetadata = true,
            IncludeChecksum = true,
            PrettyPrint = true,
            ExportedBy = httpContext.User.Identity?.Name ?? "anonymous",
            SourceSystem = Environment.MachineName
        };

        try
        {
            var manifest = await exportService.ExportTenantAsync(tenant.Id, options, cancellationToken);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{slug}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "Export failed",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Gets export preview information for a tenant (entity counts, etc.).
    /// </summary>
    [Authorize(Policy = "PlatformAdmin")]
    public static async Task<IResult> GetExportPreview(
        [FromRoute] string slug,
        [FromServices] AuthDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

        if (tenant == null)
        {
            return Results.NotFound(new { error = "Tenant not found", slug });
        }

        // Get entity counts
        var realmCount = await dbContext.Realms
            .CountAsync(r => r.TenantId == tenant.Id, cancellationToken);

        var clientCount = await dbContext.Clients
            .CountAsync(c => c.TenantId == tenant.Id, cancellationToken);

        var providerCount = await dbContext.IdentityProviders
            .CountAsync(p => p.TenantId == tenant.Id, cancellationToken);

        var scopeCount = await dbContext.Scopes
            .CountAsync(s => s.TenantId == tenant.Id, cancellationToken);

        var realmIds = await dbContext.Realms
            .Where(r => r.TenantId == tenant.Id)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var roleCount = await dbContext.Roles
            .CountAsync(r => realmIds.Contains(r.RealmId), cancellationToken);

        return Results.Ok(new
        {
            tenant = new { slug = tenant.Slug, name = tenant.Name },
            counts = new
            {
                realms = realmCount,
                clients = clientCount,
                identityProviders = providerCount,
                scopes = scopeCount,
                roles = roleCount
            }
        });
    }

    /// <summary>
    /// Maps export/import routes to the endpoint router.
    /// </summary>
    public static void MapExportImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/platform/tenants/{slug}")
            .WithTags("Export/Import");

        group.MapGet("/export", ExportTenant)
            .WithName("ExportTenant")
            .WithDescription("Export tenant configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        group.MapGet("/export/preview", GetExportPreview)
            .WithName("GetExportPreview")
            .WithDescription("Get export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);
    }
}
