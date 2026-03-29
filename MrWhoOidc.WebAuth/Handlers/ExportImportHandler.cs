using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.MultiTenancy;
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
    [Authorize(Policy = "platform-admin")]
    public static async Task<IResult> ExportTenant(
        [FromRoute] string slug,
        [FromQuery] string? mode,
        [FromServices] AuthDbContext dbContext,
        [FromServices] IConfigurationExportService exportService,
        [FromServices] ITenantAccessor tenantAccessor,
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
    [Authorize(Policy = "platform-admin")]
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
    /// Exports a realm configuration.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ExportRealm(
        [FromRoute] Guid id,
        [FromQuery] string? mode,
        [FromServices] AuthDbContext dbContext,
        [FromServices] IConfigurationExportService exportService,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var realm = await dbContext.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId.Value, cancellationToken);

        if (realm == null)
        {
            return Results.NotFound(new { error = "Realm not found", id });
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId.Value, cancellationToken);

        var options = CreateExportOptions(httpContext, mode);

        try
        {
            var manifest = await exportService.ExportRealmAsync(realm.Id, options, cancellationToken);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{realm.Name}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Export failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Gets export preview information for a realm.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> GetRealmExportPreview(
        [FromRoute] Guid id,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var realm = await dbContext.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId.Value, cancellationToken);

        if (realm == null)
        {
            return Results.NotFound(new { error = "Realm not found", id });
        }

        var clientCount = await dbContext.Clients.CountAsync(c => c.RealmId == realm.Id, cancellationToken);
        var roleCount = await dbContext.Roles.CountAsync(r => r.RealmId == realm.Id, cancellationToken);
        var clientIds = await dbContext.Clients
            .Where(c => c.RealmId == realm.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var secretCount = await dbContext.ClientSecrets.CountAsync(s => clientIds.Contains(s.ClientId), cancellationToken);

        return Results.Ok(new
        {
            realm = new { id = realm.Id, name = realm.Name, displayName = realm.DisplayName },
            counts = new
            {
                clients = clientCount,
                roles = roleCount,
                secrets = secretCount
            }
        });
    }

    /// <summary>
    /// Exports a client configuration.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ExportClient(
        [FromRoute] Guid id,
        [FromQuery] string? mode,
        [FromServices] AuthDbContext dbContext,
        [FromServices] IConfigurationExportService exportService,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var client = await dbContext.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId.Value, cancellationToken);

        if (client == null)
        {
            return Results.NotFound(new { error = "Client not found", id });
        }

        var realm = await dbContext.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == client.RealmId, cancellationToken);
        var tenant = await dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId.Value, cancellationToken);
        var options = CreateExportOptions(httpContext, mode);

        try
        {
            var manifest = await exportService.ExportClientAsync(client.Id, options, cancellationToken);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{realm?.Name ?? "realm"}-{client.ClientId}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Export failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Gets export preview information for a client.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> GetClientExportPreview(
        [FromRoute] Guid id,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var client = await dbContext.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId.Value, cancellationToken);

        if (client == null)
        {
            return Results.NotFound(new { error = "Client not found", id });
        }

        var scopeCount = await dbContext.ClientScopes.CountAsync(cs => cs.ClientId == client.Id, cancellationToken);
        var secretCount = await dbContext.ClientSecrets.CountAsync(s => s.ClientId == client.Id, cancellationToken);
        var providerCount = await dbContext.ClientIdentityProviders.CountAsync(cip => cip.ClientId == client.Id, cancellationToken);

        return Results.Ok(new
        {
            client = new { id = client.Id, clientId = client.ClientId, clientName = client.ClientName },
            counts = new
            {
                scopes = scopeCount,
                secrets = secretCount,
                identityProviders = providerCount
            }
        });
    }

    /// <summary>
    /// Exports an identity provider configuration.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ExportProvider(
        [FromRoute] Guid id,
        [FromQuery] string? mode,
        [FromServices] AuthDbContext dbContext,
        [FromServices] IConfigurationExportService exportService,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var provider = await dbContext.IdentityProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId.Value, cancellationToken);

        if (provider == null)
        {
            return Results.NotFound(new { error = "Provider not found", id });
        }

        var tenant = await dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId.Value, cancellationToken);
        var options = CreateExportOptions(httpContext, mode);

        try
        {
            var manifest = await exportService.ExportIdentityProviderAsync(provider.Id, options, cancellationToken);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{provider.Name}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Export failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Gets export preview information for a provider.
    /// </summary>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> GetProviderExportPreview(
        [FromRoute] Guid id,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            return Results.BadRequest(new { error = "Tenant context required" });
        }

        var provider = await dbContext.IdentityProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId.Value, cancellationToken);

        if (provider == null)
        {
            return Results.NotFound(new { error = "Provider not found", id });
        }

        var claimMappingCount = await dbContext.IdentityProviderClaimMappings.CountAsync(m => m.IdentityProviderId == provider.Id, cancellationToken);
        var keyCount = await dbContext.IdentityProviderKeys.CountAsync(k => k.IdentityProviderId == provider.Id, cancellationToken);
        var hasClientSecret = !string.IsNullOrEmpty(provider.ConfigJson) && provider.ConfigJson.Contains("client_secret", StringComparison.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            provider = new { id = provider.Id, name = provider.Name, displayName = provider.DisplayName, type = provider.Type.ToString() },
            counts = new
            {
                claimMappings = claimMappingCount,
                keys = keyCount,
                hasClientSecret
            }
        });
    }

    /// <summary>
    /// Previews an import operation without applying changes.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/platform/tenants/import/preview
    /// </remarks>
    [Authorize(Policy = "platform-admin")]
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
    [Authorize(Policy = "platform-admin")]
    public static async Task<IResult> ImportTenant(
        [FromBody] ImportTenantRequest request,
        [FromServices] IConfigurationImportService importService,
        [FromServices] ITenantAccessor tenantAccessor,
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
    /// Previews a realm import operation without applying changes.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/realms/import/preview
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> PreviewRealmImport(
        [FromBody] ImportRealmRequest request,
        [FromServices] IConfigurationImportService importService,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RealmJson))
        {
            return Results.BadRequest(new { error = "Realm JSON is required" });
        }

        try
        {
            var realmDefinition = JsonSerializer.Deserialize<RealmSeedDefinition>(request.RealmJson, JsonOptions);
            if (realmDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid realm JSON format" });
            }

            // Get target tenant
            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            // Check for conflicts
            var conflicts = new List<object>();
            var existingRealm = await dbContext.Realms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId.Value && r.Name == realmDefinition.Name, cancellationToken);

            if (existingRealm != null)
            {
                conflicts.Add(new
                {
                    entityType = "Realm",
                    identifier = realmDefinition.Name,
                    existingId = existingRealm.Id,
                    message = $"Realm '{realmDefinition.Name}' already exists in tenant"
                });
            }

            return Results.Ok(new
            {
                isValid = true,
                conflicts,
                summary = new
                {
                    realmName = realmDefinition.Name,
                    clientCount = realmDefinition.Clients?.Count ?? 0,
                    roleCount = realmDefinition.Roles?.Count ?? 0
                }
            });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
    }

    /// <summary>
    /// Imports a realm configuration.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/realms/import
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ImportRealm(
        [FromBody] ImportRealmRequest request,
        [FromServices] IConfigurationImportService importService,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RealmJson))
        {
            return Results.BadRequest(new { error = "Realm JSON is required" });
        }

        try
        {
            var realmDefinition = JsonSerializer.Deserialize<RealmSeedDefinition>(request.RealmJson, JsonOptions);
            if (realmDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid realm JSON format" });
            }

            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            var options = new ImportOptions
            {
                ValidateOnly = request.DryRun,
                TargetTenantId = tenantId.Value,
                DefaultConflictResolution = request.ConflictResolution ?? ConflictResolution.Skip,
                ImportedBy = httpContext.User.Identity?.Name ?? "anonymous",
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            };

            // Wrap realm definition in a manifest
            var manifest = new ExportManifest
            {
                Version = 1,
                ExportType = "realm",
                Data = new SeedManifest
                {
                    Realms = [realmDefinition]
                }
            };

            var result = await importService.ImportRealmAsync(manifest, options, cancellationToken);

            if (result.Success)
            {
                return Results.Ok(result);
            }

            return Results.BadRequest(result);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Import failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Previews a client import operation without applying changes.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/clients/import/preview
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> PreviewClientImport(
        [FromBody] ImportClientRequest request,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ClientJson))
        {
            return Results.BadRequest(new { error = "Client JSON is required" });
        }

        try
        {
            var clientDefinition = JsonSerializer.Deserialize<ClientSeedDefinition>(request.ClientJson, JsonOptions);
            if (clientDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid client JSON format" });
            }

            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            // Validate target realm exists
            if (request.TargetRealmId == null)
            {
                return Results.BadRequest(new { error = "Target realm ID is required" });
            }

            var realm = await dbContext.Realms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.TargetRealmId.Value && r.TenantId == tenantId.Value, cancellationToken);

            if (realm == null)
            {
                return Results.BadRequest(new { error = "Target realm not found or not accessible" });
            }

            // Check for conflicts
            var conflicts = new List<object>();
            var existingClient = await dbContext.Clients
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId.Value && c.ClientId == clientDefinition.ClientId, cancellationToken);

            if (existingClient != null)
            {
                conflicts.Add(new
                {
                    entityType = "Client",
                    identifier = clientDefinition.ClientId,
                    existingId = existingClient.Id,
                    message = $"Client '{clientDefinition.ClientId}' already exists in tenant"
                });
            }

            return Results.Ok(new
            {
                isValid = true,
                conflicts,
                summary = new
                {
                    clientId = clientDefinition.ClientId,
                    clientName = clientDefinition.ClientName,
                    targetRealm = realm.Name,
                    scopeCount = clientDefinition.AllowedScopes?.Count ?? 0
                }
            });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
    }

    /// <summary>
    /// Imports a client configuration.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/clients/import
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ImportClient(
        [FromBody] ImportClientRequest request,
        [FromServices] IConfigurationImportService importService,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ClientJson))
        {
            return Results.BadRequest(new { error = "Client JSON is required" });
        }

        if (request.TargetRealmId == null)
        {
            return Results.BadRequest(new { error = "Target realm ID is required" });
        }

        try
        {
            var clientDefinition = JsonSerializer.Deserialize<ClientSeedDefinition>(request.ClientJson, JsonOptions);
            if (clientDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid client JSON format" });
            }

            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            // Get target realm name for the manifest
            var targetRealm = await dbContext.Realms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.TargetRealmId.Value && r.TenantId == tenantId.Value, cancellationToken);

            if (targetRealm == null)
            {
                return Results.BadRequest(new { error = "Target realm not found or not accessible" });
            }

            // Create a new client definition with the target realm set
            var clientWithRealm = clientDefinition with { Realm = targetRealm.Name };

            var options = new ImportOptions
            {
                ValidateOnly = request.DryRun,
                TargetTenantId = tenantId.Value,
                DefaultConflictResolution = request.ConflictResolution ?? ConflictResolution.Skip,
                Secrets = string.IsNullOrEmpty(request.ClientSecret)
                    ? []
                    : new Dictionary<string, string> { [clientDefinition.ClientId ?? ""] = request.ClientSecret },
                ImportedBy = httpContext.User.Identity?.Name ?? "anonymous",
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            };

            // Wrap client definition in a manifest
            var manifest = new ExportManifest
            {
                Version = 1,
                ExportType = "client",
                Data = new SeedManifest
                {
                    Clients = [clientWithRealm]
                }
            };

            var result = await importService.ImportClientAsync(manifest, options, cancellationToken);

            if (result.Success)
            {
                return Results.Ok(result);
            }

            return Results.BadRequest(result);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Import failed", statusCode: 500);
        }
    }

    /// <summary>
    /// Previews an identity provider import operation without applying changes.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/providers/import/preview
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> PreviewProviderImport(
        [FromBody] ImportProviderRequest request,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ProviderJson))
        {
            return Results.BadRequest(new { error = "Provider JSON is required" });
        }

        try
        {
            var providerDefinition = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(request.ProviderJson, JsonOptions);
            if (providerDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid provider JSON format" });
            }

            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            // Check for conflicts
            var conflicts = new List<object>();
            var existingProvider = await dbContext.IdentityProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId.Value && p.Name == providerDefinition.Name, cancellationToken);

            if (existingProvider != null)
            {
                conflicts.Add(new
                {
                    entityType = "IdentityProvider",
                    identifier = providerDefinition.Name,
                    existingId = existingProvider.Id,
                    message = $"Identity provider '{providerDefinition.Name}' already exists in tenant"
                });
            }

            return Results.Ok(new
            {
                isValid = true,
                conflicts,
                summary = new
                {
                    providerName = providerDefinition.Name,
                    providerType = providerDefinition.Type?.ToString(),
                    claimMappingCount = providerDefinition.ClaimMappings?.Count ?? 0
                }
            });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
    }

    /// <summary>
    /// Imports an identity provider configuration.
    /// </summary>
    /// <remarks>
    /// POST /admin/api/providers/import
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> ImportProvider(
        [FromBody] ImportProviderRequest request,
        [FromServices] IConfigurationImportService importService,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ProviderJson))
        {
            return Results.BadRequest(new { error = "Provider JSON is required" });
        }

        try
        {
            var providerDefinition = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(request.ProviderJson, JsonOptions);
            if (providerDefinition == null)
            {
                return Results.BadRequest(new { error = "Invalid provider JSON format" });
            }

            var tenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
            if (tenantId == null)
            {
                return Results.BadRequest(new { error = "Tenant context required" });
            }

            var options = new ImportOptions
            {
                ValidateOnly = request.DryRun,
                TargetTenantId = tenantId.Value,
                DefaultConflictResolution = request.ConflictResolution ?? ConflictResolution.Skip,
                Secrets = string.IsNullOrEmpty(request.ClientSecret)
                    ? []
                    : new Dictionary<string, string> { [providerDefinition.Name ?? ""] = request.ClientSecret },
                ImportedBy = httpContext.User.Identity?.Name ?? "anonymous",
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            };

            // Wrap provider definition in a manifest
            var manifest = new ExportManifest
            {
                Version = 1,
                ExportType = "provider",
                Data = new SeedManifest
                {
                    IdentityProviders = [providerDefinition]
                }
            };

            var result = await importService.ImportIdentityProviderAsync(manifest, options, cancellationToken);

            if (result.Success)
            {
                return Results.Ok(result);
            }

            return Results.BadRequest(result);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Import failed", statusCode: 500);
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

        var tenantRealmExportGroup = endpoints.MapGroup("/t/{slug}/admin/api/realms/{id:guid}")
            .WithTags("Export/Import");

        var tenantClientExportGroup = endpoints.MapGroup("/t/{slug}/admin/api/clients/{id:guid}")
            .WithTags("Export/Import");

        var tenantProviderExportGroup = endpoints.MapGroup("/t/{slug}/admin/api/providers/{id:guid}")
            .WithTags("Export/Import");

        var realmExportGroup = endpoints.MapGroup("/admin/api/realms/{id:guid}")
            .WithTags("Export/Import");

        var clientExportGroup = endpoints.MapGroup("/admin/api/clients/{id:guid}")
            .WithTags("Export/Import");

        var providerExportGroup = endpoints.MapGroup("/admin/api/providers/{id:guid}")
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

        realmExportGroup.MapGet("/export", ExportRealm)
            .WithName("ExportRealm")
            .WithDescription("Export realm configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        realmExportGroup.MapGet("/export/preview", GetRealmExportPreview)
            .WithName("GetRealmExportPreview")
            .WithDescription("Get realm export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        clientExportGroup.MapGet("/export", ExportClient)
            .WithName("ExportClient")
            .WithDescription("Export client configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        clientExportGroup.MapGet("/export/preview", GetClientExportPreview)
            .WithName("GetClientExportPreview")
            .WithDescription("Get client export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        providerExportGroup.MapGet("/export", ExportProvider)
            .WithName("ExportProvider")
            .WithDescription("Export identity provider configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        providerExportGroup.MapGet("/export/preview", GetProviderExportPreview)
            .WithName("GetProviderExportPreview")
            .WithDescription("Get provider export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        tenantRealmExportGroup.MapGet("/export", ExportRealm)
            .WithName("TenantExportRealm")
            .WithDescription("Export realm configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        tenantRealmExportGroup.MapGet("/export/preview", GetRealmExportPreview)
            .WithName("TenantGetRealmExportPreview")
            .WithDescription("Get realm export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        tenantClientExportGroup.MapGet("/export", ExportClient)
            .WithName("TenantExportClient")
            .WithDescription("Export client configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        tenantClientExportGroup.MapGet("/export/preview", GetClientExportPreview)
            .WithName("TenantGetClientExportPreview")
            .WithDescription("Get client export preview with entity counts")
            .Produces<object>(200)
            .Produces(404);

        tenantProviderExportGroup.MapGet("/export", ExportProvider)
            .WithName("TenantExportProvider")
            .WithDescription("Export identity provider configuration as JSON")
            .Produces(200, contentType: "application/json")
            .Produces(404)
            .Produces(500);

        tenantProviderExportGroup.MapGet("/export/preview", GetProviderExportPreview)
            .WithName("TenantGetProviderExportPreview")
            .WithDescription("Get provider export preview with entity counts")
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

        // Realm import endpoints (tenant-level)
        var realmImportGroup = endpoints.MapGroup("/admin/api/realms/import")
            .WithTags("Export/Import");

        realmImportGroup.MapPost("/preview", PreviewRealmImport)
            .WithName("PreviewRealmImport")
            .WithDescription("Preview realm import operation without applying changes")
            .Produces<object>(200)
            .Produces(400);

        realmImportGroup.MapPost("/", ImportRealm)
            .WithName("ImportRealm")
            .WithDescription("Import realm configuration from JSON")
            .Produces<ImportResult>(200)
            .Produces(400)
            .Produces(500);

        // Client import endpoints (tenant-level)
        var clientImportGroup = endpoints.MapGroup("/admin/api/clients/import")
            .WithTags("Export/Import");

        clientImportGroup.MapPost("/preview", PreviewClientImport)
            .WithName("PreviewClientImport")
            .WithDescription("Preview client import operation without applying changes")
            .Produces<object>(200)
            .Produces(400);

        clientImportGroup.MapPost("/", ImportClient)
            .WithName("ImportClient")
            .WithDescription("Import client configuration from JSON")
            .Produces<ImportResult>(200)
            .Produces(400)
            .Produces(500);

        // Identity provider import endpoints (tenant-level)
        var providerImportGroup = endpoints.MapGroup("/admin/api/providers/import")
            .WithTags("Export/Import");

        providerImportGroup.MapPost("/preview", PreviewProviderImport)
            .WithName("PreviewProviderImport")
            .WithDescription("Preview identity provider import operation without applying changes")
            .Produces<object>(200)
            .Produces(400);

        providerImportGroup.MapPost("/", ImportProvider)
            .WithName("ImportProvider")
            .WithDescription("Import identity provider configuration from JSON")
            .Produces<ImportResult>(200)
            .Produces(400)
            .Produces(500);

        // Configuration audit log endpoints
        var auditGroup = endpoints.MapGroup("/admin/api/configuration-audit")
            .WithTags("Export/Import");

        auditGroup.MapGet("/", GetAuditLogs)
            .WithName("GetConfigurationAuditLogs")
            .WithDescription("Get list of configuration export/import audit logs")
            .Produces<IEnumerable<object>>(200);

        auditGroup.MapGet("/{id:guid}", GetAuditLogDetail)
            .WithName("GetConfigurationAuditLogDetail")
            .WithDescription("Get details of a specific audit log entry")
            .Produces<object>(200)
            .Produces(404);

        // Tenant-prefixed audit group (so CLI calls via /t/{slug}/admin/api/... work)
        var tenantAuditGroup = endpoints.MapGroup("/t/{slug}/admin/api/configuration-audit")
            .WithTags("Export/Import");

        tenantAuditGroup.MapGet("/", GetAuditLogs)
            .WithName("TenantGetConfigurationAuditLogs")
            .Produces<IEnumerable<object>>(200);

        tenantAuditGroup.MapGet("/{id:guid}", GetAuditLogDetail)
            .WithName("TenantGetConfigurationAuditLogDetail")
            .Produces<object>(200)
            .Produces(404);
    }

    /// <summary>
    /// Gets a list of configuration export/import audit log entries.
    /// </summary>
    /// <remarks>
    /// GET /admin/api/configuration-audit?tenantId={guid}&amp;operation={export|import}&amp;page=1&amp;pageSize=20
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> GetAuditLogs(
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        [FromQuery] Guid? tenantId = null,
        [FromQuery] string? operation = null,
        [FromQuery] string? entityType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        // Restrict to current tenant if not platform admin
        var contextTenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        var isPlatformAdmin = httpContext.User.IsInRole("PlatformAdmin");

        var query = dbContext.Set<MrWhoOidc.Auth.Seeding.ConfigurationAuditLog>().AsNoTracking();

        // Filter by tenant - platform admins can see all, tenant admins only their tenant
        if (isPlatformAdmin && tenantId.HasValue)
        {
            query = query.Where(a => a.TenantId == tenantId.Value);
        }
        else if (!isPlatformAdmin && contextTenantId.HasValue)
        {
            query = query.Where(a => a.TenantId == contextTenantId.Value);
        }

        // Additional filters
        if (!string.IsNullOrEmpty(operation))
        {
            query = query.Where(a => a.Operation == operation);
        }

        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        // Ensure valid pagination
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.TenantId,
                a.Operation,
                a.EntityType,
                a.EntityIdentifier,
                a.ExportMode,
                a.Result,
                a.PerformedBy,
                a.Timestamp
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            items,
            pagination = new
            {
                page,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
            }
        });
    }

    /// <summary>
    /// Gets details of a specific audit log entry.
    /// </summary>
    /// <remarks>
    /// GET /admin/api/configuration-audit/{id}
    /// </remarks>
    [Authorize(Policy = "tenant-admin")]
    public static async Task<IResult> GetAuditLogDetail(
        [FromRoute] Guid id,
        [FromServices] AuthDbContext dbContext,
        [FromServices] ITenantAccessor tenantAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var contextTenantId = httpContext.Items["TenantId"] as Guid? ?? tenantAccessor.CurrentTenant?.TenantId;
        var isPlatformAdmin = httpContext.User.IsInRole("PlatformAdmin");

        var auditLog = await dbContext.Set<MrWhoOidc.Auth.Seeding.ConfigurationAuditLog>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (auditLog == null)
        {
            return Results.NotFound(new { error = "Audit log entry not found", id });
        }

        // Check tenant access (non-platform admins can only see their tenant's logs)
        if (!isPlatformAdmin && auditLog.TenantId != contextTenantId)
        {
            return Results.NotFound(new { error = "Audit log entry not found", id });
        }

        return Results.Ok(new
        {
            auditLog.Id,
            auditLog.TenantId,
            auditLog.Operation,
            auditLog.EntityType,
            auditLog.EntityIdentifier,
            auditLog.ExportMode,
            auditLog.Result,
            auditLog.EntitiesCreated,
            auditLog.EntitiesUpdated,
            auditLog.EntitiesSkipped,
            auditLog.ErrorDetails,
            auditLog.ManifestChecksum,
            auditLog.PerformedBy,
            auditLog.PerformedByUserId,
            auditLog.IpAddress,
            auditLog.UserAgent,
            auditLog.Timestamp
        });
    }

    private static ExportOptions CreateExportOptions(HttpContext httpContext, string? mode)
    {
        var exportMode = mode?.ToLowerInvariant() switch
        {
            "full" => ExportMode.Full,
            _ => ExportMode.Obfuscated
        };

        return new ExportOptions
        {
            Mode = exportMode,
            IncludeMetadata = true,
            IncludeChecksum = true,
            PrettyPrint = true,
            ExportedBy = httpContext.User.Identity?.Name ?? "anonymous",
            SourceSystem = Environment.MachineName
        };
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

/// <summary>
/// Request model for realm import.
/// </summary>
public sealed record ImportRealmRequest
{
    /// <summary>
    /// The JSON representation of the realm to import.
    /// </summary>
    public string? RealmJson { get; init; }

    /// <summary>
    /// Whether this is a dry run (validate without applying changes).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Conflict resolution strategy.
    /// </summary>
    public ConflictResolution? ConflictResolution { get; init; }
}

/// <summary>
/// Request model for client import.
/// </summary>
public sealed record ImportClientRequest
{
    /// <summary>
    /// The JSON representation of the client to import.
    /// </summary>
    public string? ClientJson { get; init; }

    /// <summary>
    /// The target realm ID to import the client into.
    /// </summary>
    public Guid? TargetRealmId { get; init; }

    /// <summary>
    /// Whether this is a dry run (validate without applying changes).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Conflict resolution strategy.
    /// </summary>
    public ConflictResolution? ConflictResolution { get; init; }

    /// <summary>
    /// The client secret to use (required for confidential clients).
    /// </summary>
    public string? ClientSecret { get; init; }
}

/// <summary>
/// Request model for identity provider import.
/// </summary>
public sealed record ImportProviderRequest
{
    /// <summary>
    /// The JSON representation of the identity provider to import.
    /// </summary>
    public string? ProviderJson { get; init; }

    /// <summary>
    /// Whether this is a dry run (validate without applying changes).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Conflict resolution strategy.
    /// </summary>
    public ConflictResolution? ConflictResolution { get; init; }

    /// <summary>
    /// The client secret to use for the identity provider.
    /// </summary>
    public string? ClientSecret { get; init; }
}
