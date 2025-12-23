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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
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
    /// Previews an import operation without applying changes.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/platform/tenants/import/preview
    /// </remarks>
    [Authorize(Policy = "PlatformAdmin")]
    public static async Task<IResult> PreviewImport(
        [FromBody] ImportPreviewRequest request,
        [FromServices] IConfigurationImportService importService,
        CancellationToken cancellationToken)
    {
        if (request.Manifest == null)
        {
            return Results.BadRequest(new { error = "Manifest is required" });
        }

        try
        {
            // Parse the manifest from JSON
            var manifest = JsonSerializer.Deserialize<ExportManifest>(request.Manifest, JsonOptions);
            if (manifest == null)
            {
                return Results.BadRequest(new { error = "Invalid manifest format" });
            }

            var options = new ImportOptions
            {
                ValidateOnly = true,
                DefaultConflictResolution = request.DefaultConflictResolution ?? ConflictResolution.Skip
            };

            var preview = await importService.PreviewImportAsync(manifest, options, cancellationToken);

            return Results.Ok(preview);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new
            {
                error = "Invalid JSON format",
                details = ex.Message
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "Preview failed",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Imports tenant configuration from a manifest.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/platform/tenants/import
    /// </remarks>
    [Authorize(Policy = "PlatformAdmin")]
    public static async Task<IResult> ImportTenant(
        [FromBody] ImportTenantRequest request,
        [FromServices] IConfigurationImportService importService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request.Manifest == null)
        {
            return Results.BadRequest(new { error = "Manifest is required" });
        }

        try
        {
            // Parse the manifest from JSON
            var manifest = JsonSerializer.Deserialize<ExportManifest>(request.Manifest, JsonOptions);
            if (manifest == null)
            {
                return Results.BadRequest(new { error = "Invalid manifest format" });
            }

            // Build conflict resolutions dictionary
            var conflictOverrides = new Dictionary<string, ConflictResolution>();
            if (request.ConflictResolutions != null)
            {
                foreach (var (key, value) in request.ConflictResolutions)
                {
                    if (Enum.TryParse<ConflictResolution>(value, true, out var resolution))
                    {
                        conflictOverrides[key] = resolution;
                    }
                }
            }

            var options = new ImportOptions
            {
                ValidateOnly = request.DryRun,
                DefaultConflictResolution = request.DefaultConflictResolution ?? ConflictResolution.Skip,
                ConflictOverrides = conflictOverrides,
                Secrets = request.Secrets ?? [],
                ImportedBy = httpContext.User.Identity?.Name ?? "anonymous",
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            };

            var result = await importService.ImportTenantAsync(manifest, options, cancellationToken);

            if (result.Success)
            {
                return Results.Ok(result);
            }

            return Results.BadRequest(result);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new
            {
                error = "Invalid JSON format",
                details = ex.Message
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "Import failed",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Maps export/import routes to the endpoint router.
    /// </summary>
    public static void MapExportImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Export endpoints (tenant-specific)
        var exportGroup = endpoints.MapGroup("/admin/api/platform/tenants/{slug}")
            .WithTags("Export/Import");

        exportGroup.MapGet("/export", ExportTenant)
            .WithName("ExportTenant")
            .WithDescription("Export tenant configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        exportGroup.MapGet("/export/preview", GetExportPreview)
            .WithName("GetExportPreview")
            .WithDescription("Get export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        // Import endpoints (platform-level)
        var importGroup = endpoints.MapGroup("/admin/api/platform/tenants/import")
            .WithTags("Export/Import");

        importGroup.MapPost("/preview", PreviewImport)
            .WithName("PreviewImport")
            .WithDescription("Preview import operation without applying changes")
            .Produces<ImportPreview>(200)
            .Produces(400)
            .Produces(500);

        importGroup.MapPost("/", ImportTenant)
            .WithName("ImportTenant")
            .WithDescription("Import tenant configuration from manifest")
            .Produces<ImportResult>(200)
            .Produces(400)
            .Produces(500);
    }
}

/// <summary>
/// Request model for import preview.
/// </summary>
public sealed record ImportPreviewRequest
{
    /// <summary>
    /// The JSON manifest string to preview.
    /// </summary>
    public string? Manifest { get; init; }

    /// <summary>
    /// Default conflict resolution strategy.
    /// </summary>
    public ConflictResolution? DefaultConflictResolution { get; init; }
}

/// <summary>
/// Request model for tenant import.
/// </summary>
public sealed record ImportTenantRequest
{
    /// <summary>
    /// The JSON manifest string to import.
    /// </summary>
    public string? Manifest { get; init; }

    /// <summary>
    /// Whether this is a dry run (validate without applying changes).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Default conflict resolution strategy.
    /// </summary>
    public ConflictResolution? DefaultConflictResolution { get; init; }

    /// <summary>
    /// Per-entity conflict resolutions.
    /// Key format: "{EntityType}:{Identifier}" (e.g., "tenant:my-tenant").
    /// Value: "Skip", "Rename", "Merge", or "Overwrite".
    /// </summary>
    public Dictionary<string, string>? ConflictResolutions { get; init; }

    /// <summary>
    /// Secrets for obfuscated entities.
    /// Key format: "{EntityId}" (e.g., client ID or provider name).
    /// Value: The actual secret value to use.
    /// </summary>
    public Dictionary<string, string>? Secrets { get; init; }
}
