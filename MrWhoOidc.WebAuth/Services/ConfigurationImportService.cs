using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for importing OIDC configuration from portable JSON format.
/// </summary>
public sealed class ConfigurationImportService(
    AuthDbContext dbContext,
    ILogger<ConfigurationImportService> logger) : IConfigurationImportService
{
    private readonly AuthDbContext _dbContext = dbContext;
    private readonly ILogger<ConfigurationImportService> _logger = logger;

    private static readonly HashSet<string> AutoSeedableGlobalScopes = new(StringComparer.Ordinal)
    {
        OidcConstants.Scopes.OpenId,
        OidcConstants.Scopes.Profile,
        OidcConstants.Scopes.Email,
        OidcConstants.Scopes.Address,
        OidcConstants.Scopes.Phone,
        OidcConstants.Scopes.OfflineAccess,
        OidcConstants.Scopes.Roles,
        OidcConstants.Scopes.Tenants
    };

    /// <inheritdoc />
    public async Task<ImportPreview> PreviewImportAsync(
        ExportManifest manifest,
        ImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Previewing import of {ExportType}", manifest.ExportType);

        options ??= new ImportOptions();
        var preview = new ImportPreview();

        // Validate manifest structure
        var validationErrors = ValidateManifest(manifest);
        if (validationErrors.Count > 0)
        {
            preview = preview with
            {
                IsValid = false,
                ValidationErrors = validationErrors
            };
            return preview;
        }

        // Validate checksum if present
        if (manifest.Metadata?.Checksum != null && manifest.Data != null)
        {
            var calculatedChecksum = GenerateChecksum(manifest.Data);
            if (!string.Equals(calculatedChecksum, manifest.Metadata.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                preview = preview with
                {
                    IsValid = false,
                    ValidationErrors = [new ValidationError
                    {
                        Path = "metadata.checksum",
                        Code = "CHECKSUM_MISMATCH",
                        Message = "Manifest checksum does not match computed checksum",
                        Severity = ValidationSeverity.Error
                    }]
                };
                return preview;
            }
        }

        // Check for obfuscated secrets requiring replacement
        var warnings = new List<string>();
        if (manifest.ExportMode == "obfuscated")
        {
            // Check clients for obfuscated secrets
            foreach (var tenant in manifest.Data?.Tenants ?? [])
            {
                foreach (var client in tenant.Clients ?? [])
                {
                    if (ExportManifest.IsObfuscated(client.ClientSecretHash))
                    {
                        warnings.Add($"Client '{client.ClientId}' has an obfuscated secret. Provide a replacement in import options.");
                    }
                }

                foreach (var provider in tenant.IdentityProviders ?? [])
                {
                    if (provider.Config?.Any(kv => 
                        kv.Value is JsonElement je && je.GetString() == ExportManifest.ObfuscatedMarker) == true)
                    {
                        warnings.Add($"Provider '{provider.Name}' has obfuscated configuration. Provide replacement values in import options.");
                    }
                }
            }

            // Check standalone providers for obfuscated secrets
            foreach (var provider in manifest.Data?.IdentityProviders ?? [])
            {
                if (provider.Config?.Any(kv => 
                    kv.Value is JsonElement je && je.GetString() == ExportManifest.ObfuscatedMarker) == true)
                {
                    warnings.Add($"Provider '{provider.Name}' has obfuscated configuration. Provide replacement values in import options.");
                }
            }
        }

        // Detect conflicts
        var conflicts = await DetectConflictsAsync(manifest, options, cancellationToken);
        
        // Build entity lists
        var (toCreate, toUpdate) = BuildEntityLists(manifest, conflicts, options);

        // Calculate counts
        var tenantCount = manifest.Data?.Tenants?.Count ?? 0;
        var realmCount = (manifest.Data?.Realms?.Count ?? 0) + 
                         (manifest.Data?.Tenants?.Sum(t => t.Realms?.Count ?? 0) ?? 0);
        var clientCount = (manifest.Data?.Clients?.Count ?? 0) + 
                          (manifest.Data?.Tenants?.Sum(t => t.Clients?.Count ?? 0) ?? 0);
        var providerCount = (manifest.Data?.IdentityProviders?.Count ?? 0) + 
                            (manifest.Data?.Tenants?.Sum(t => t.IdentityProviders?.Count ?? 0) ?? 0);
        var scopeCount = manifest.Data?.Scopes?.Count ?? 0;

        preview = preview with
        {
            IsValid = true,
            Conflicts = conflicts,
            EntitiesToCreate = toCreate,
            EntitiesToUpdate = toUpdate,
            Warnings = warnings,
            TenantCount = tenantCount,
            RealmCount = realmCount,
            ClientCount = clientCount,
            ProviderCount = providerCount,
            ScopeCount = scopeCount,
            HasObfuscatedSecrets = warnings.Any(w => w.Contains("obfuscated", StringComparison.OrdinalIgnoreCase)),
            ObfuscatedSecretCount = warnings.Count(w => w.Contains("obfuscated", StringComparison.OrdinalIgnoreCase))
        };

        return preview;
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportTenantAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (manifest.ExportType != "tenant")
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Manifest",
                Identifier = "exportType",
                Code = "INVALID_TYPE",
                Message = $"Expected export type 'tenant' but got '{manifest.ExportType}'"
            });
        }

        // Run preview first
        var preview = await PreviewImportAsync(manifest, options, cancellationToken);
        if (!preview.IsValid)
        {
            return ImportResult.Failed(preview.ValidationErrors.Select(e => new ImportError
            {
                EntityType = "Validation",
                Code = e.Code,
                Message = e.Message
            }).ToArray());
        }

        if (options.ValidateOnly)
        {
            return new ImportResult
            {
                Success = true,
                Warnings = preview.Warnings
            };
        }

        _logger.LogInformation("Importing tenant from manifest");

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var errors = new List<ImportError>();

        using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var tenantDef in manifest.Data?.Tenants ?? [])
            {
                var existingTenant = await _dbContext.Tenants
                    .FirstOrDefaultAsync(t => t.Slug == tenantDef.Slug, cancellationToken);

                var conflict = preview.Conflicts.FirstOrDefault(c =>
                    c.Type == ConflictType.TenantSlugExists && c.Identifier == tenantDef.Slug);

                if (existingTenant != null)
                {
                    var resolution = conflict?.Resolution ?? options.GetResolution("Tenant", tenantDef.Slug);

                    switch (resolution)
                    {
                        case ConflictResolution.Skip:
                            skipped++;
                            _logger.LogInformation("Skipped existing tenant {Slug}", tenantDef.Slug);
                            continue;

                        case ConflictResolution.Overwrite:
                            UpdateTenant(existingTenant, tenantDef);
                            await _dbContext.SaveChangesAsync(cancellationToken);
                            updated++;
                            _logger.LogInformation("Updated existing tenant {Slug}", tenantDef.Slug);
                            break;

                        case ConflictResolution.Merge:
                            // Merge: update only non-null/non-default values
                            MergeTenant(existingTenant, tenantDef);
                            await _dbContext.SaveChangesAsync(cancellationToken);
                            updated++;
                            _logger.LogInformation("Merged tenant {Slug}", tenantDef.Slug);
                            break;

                        case ConflictResolution.Rename:
                            var newSlug = conflict?.SuggestedRename ?? $"{tenantDef.Slug}_imported";
                            var newTenant = CreateTenant(tenantDef with { Slug = newSlug });
                            _dbContext.Tenants.Add(newTenant);
                            await _dbContext.SaveChangesAsync(cancellationToken);
                            created++;
                            _logger.LogInformation("Created renamed tenant {NewSlug} (was {OldSlug})", newSlug, tenantDef.Slug);
                            break;
                    }
                }
                else
                {
                    var tenant = CreateTenant(tenantDef);
                    _dbContext.Tenants.Add(tenant);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    created++;
                    _logger.LogInformation("Created tenant {Slug}", tenantDef.Slug);

                    // Import nested entities
                    await ImportRealmsAsync(tenant.Id, tenantDef.Realms ?? [], options, cancellationToken);
                    await ImportClientsAsync(tenant.Id, tenantDef, options, cancellationToken);
                    await ImportIdentityProvidersAsync(tenant.Id, tenantDef.IdentityProviders ?? [], options, cancellationToken);
                }
            }

            // Import scopes
            foreach (var scopeDef in manifest.Data?.Scopes ?? [])
            {
                var existingScope = await _dbContext.Scopes
                    .FirstOrDefaultAsync(s => s.Name == scopeDef.Name, cancellationToken);

                if (existingScope == null)
                {
                    var scope = new Scope
                    {
                        Name = scopeDef.Name,
                        Description = scopeDef.Description,
                        IsExposed = scopeDef.IsExposed ?? true
                    };
                    _dbContext.Scopes.Add(scope);
                    created++;
                }
                else
                {
                    skipped++;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Log audit
            var auditLogId = await LogImportAuditAsync(
                null, "Tenant", manifest.Data?.Tenants?.FirstOrDefault()?.Slug ?? "unknown",
                options, true, created, updated, skipped, null, manifest.Metadata?.Checksum,
                cancellationToken);

            return new ImportResult
            {
                Success = true,
                EntitiesCreated = created,
                EntitiesUpdated = updated,
                EntitiesSkipped = skipped,
                Warnings = preview.Warnings,
                AuditLogId = auditLogId
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to import tenant");

            await LogImportAuditAsync(
                null, "Tenant", manifest.Data?.Tenants?.FirstOrDefault()?.Slug ?? "unknown",
                options, false, created, updated, skipped, ex.Message, manifest.Metadata?.Checksum,
                cancellationToken);

            return ImportResult.Failed(new ImportError
            {
                EntityType = "Tenant",
                Code = "IMPORT_FAILED",
                Message = ex.Message
            });
        }
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportRealmAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Require target tenant ID for realm imports
        if (!options.TargetTenantId.HasValue)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Realm",
                Code = "MISSING_TARGET_TENANT",
                Message = "Target tenant ID must be specified when importing realms"
            });
        }

        var tenantId = options.TargetTenantId.Value;

        // Verify tenant exists
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant == null)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Realm",
                Code = "TENANT_NOT_FOUND",
                Message = $"Target tenant with ID '{tenantId}' was not found"
            });
        }

        // Collect realms from both standalone and tenant-nested sources
        var realms = new List<RealmSeedDefinition>();
        realms.AddRange(manifest.Data?.Realms ?? []);
        foreach (var tenantDef in manifest.Data?.Tenants ?? [])
        {
            realms.AddRange(tenantDef.Realms ?? []);
        }

        if (realms.Count == 0)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Realm",
                Code = "NO_REALMS",
                Message = "No realms found in the manifest"
            });
        }

        if (options.ValidateOnly)
        {
            var (created, updated, skipped) = await CountRealmOperationsAsync(tenantId, realms, options, cancellationToken);
            return new ImportResult
            {
                Success = true,
                EntitiesCreated = created,
                EntitiesUpdated = updated,
                EntitiesSkipped = skipped
            };
        }

        var createdCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var errors = new List<ImportError>();

        var strategy = _dbContext.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                foreach (var realmDef in realms)
                {
                    var existingRealm = await _dbContext.Realms
                        .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == realmDef.Name, cancellationToken);

                    if (existingRealm != null)
                    {
                        var resolution = options.GetResolution("Realm", realmDef.Name);

                        switch (resolution)
                        {
                            case ConflictResolution.Skip:
                                skippedCount++;
                                _logger.LogInformation("Skipped existing realm {Name}", realmDef.Name);
                                continue;

                            case ConflictResolution.Overwrite:
                                await UpdateRealmAsync(existingRealm, realmDef, options, cancellationToken);
                                updatedCount++;
                                _logger.LogInformation("Updated existing realm {Name}", realmDef.Name);
                                break;

                            case ConflictResolution.Merge:
                                await MergeRealmAsync(existingRealm, realmDef, options, cancellationToken);
                                updatedCount++;
                                _logger.LogInformation("Merged realm {Name}", realmDef.Name);
                                break;

                            case ConflictResolution.Rename:
                                var newName = await GenerateUniqueRealmNameAsync(tenantId, realmDef.Name, cancellationToken);
                                await CreateRealmAsync(tenantId, realmDef with { Name = newName }, options, cancellationToken);
                                createdCount++;
                                _logger.LogInformation("Created renamed realm {NewName} (was {OldName})", newName, realmDef.Name);
                                break;
                        }
                    }
                    else
                    {
                        await CreateRealmAsync(tenantId, realmDef, options, cancellationToken);
                        createdCount++;
                        _logger.LogInformation("Created realm {Name}", realmDef.Name);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            });

            await LogImportAuditAsync(
                tenantId, "Realm", realms.FirstOrDefault()?.Name ?? "unknown",
                options, true, createdCount, updatedCount, skippedCount, null, manifest.Metadata?.Checksum,
                cancellationToken);

            return new ImportResult
            {
                Success = true,
                EntitiesCreated = createdCount,
                EntitiesUpdated = updatedCount,
                EntitiesSkipped = skippedCount,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import realms");

            await LogImportAuditAsync(
                tenantId, "Realm", realms.FirstOrDefault()?.Name ?? "unknown",
                options, false, createdCount, updatedCount, skippedCount, ex.Message, manifest.Metadata?.Checksum,
                cancellationToken);

            return ImportResult.Failed(new ImportError
            {
                EntityType = "Realm",
                Code = "IMPORT_FAILED",
                Message = ex.Message
            });
        }
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportClientAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Require target tenant ID for client imports
        if (!options.TargetTenantId.HasValue)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Client",
                Code = "MISSING_TARGET_TENANT",
                Message = "Target tenant ID must be specified when importing clients"
            });
        }

        var tenantId = options.TargetTenantId.Value;

        // Verify tenant exists
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant == null)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Client",
                Code = "TENANT_NOT_FOUND",
                Message = $"Target tenant with ID '{tenantId}' was not found"
            });
        }

        // Collect clients from both standalone and tenant/realm-nested sources
        var clients = new List<(ClientSeedDefinition Client, string? RealmName)>();
        foreach (var clientDef in manifest.Data?.Clients ?? [])
        {
            clients.Add((clientDef, clientDef.Realm));
        }
        foreach (var tenantDef in manifest.Data?.Tenants ?? [])
        {
            foreach (var clientDef in tenantDef.Clients ?? [])
            {
                clients.Add((clientDef, clientDef.Realm));
            }
            foreach (var realmDef in tenantDef.Realms ?? [])
            {
                foreach (var clientDef in realmDef.Clients ?? [])
                {
                    clients.Add((clientDef, realmDef.Name));
                }
            }
        }
        foreach (var realmDef in manifest.Data?.Realms ?? [])
        {
            foreach (var clientDef in realmDef.Clients ?? [])
            {
                clients.Add((clientDef, realmDef.Name));
            }
        }

        if (clients.Count == 0)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "Client",
                Code = "NO_CLIENTS",
                Message = "No clients found in the manifest"
            });
        }

        if (options.ValidateOnly)
        {
            var (created, updated, skipped) = await CountClientOperationsAsync(tenantId, clients, options, cancellationToken);
            return new ImportResult
            {
                Success = true,
                EntitiesCreated = created,
                EntitiesUpdated = updated,
                EntitiesSkipped = skipped
            };
        }

        var createdCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var errors = new List<ImportError>();

        var strategy = _dbContext.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                // Load realm mappings
                var realmMap = await _dbContext.Realms
                    .Where(r => r.TenantId == tenantId)
                    .ToDictionaryAsync(r => r.Name, r => r.Id, cancellationToken);

                foreach (var (clientDef, realmName) in clients)
                {
                    // Determine target realm
                    var targetRealmName = realmName ?? clientDef.Realm ?? "admin";
                    if (!realmMap.TryGetValue(targetRealmName, out var realmId))
                    {
                        // If target realm specified in options, use that
                        if (options.TargetRealmId.HasValue)
                        {
                            realmId = options.TargetRealmId.Value;
                        }
                        else
                        {
                            errors.Add(new ImportError
                            {
                                EntityType = "Client",
                                Identifier = clientDef.ClientId,
                                Code = "REALM_NOT_FOUND",
                                Message = $"Target realm '{targetRealmName}' not found for client '{clientDef.ClientId}'"
                            });
                            skippedCount++;
                            continue;
                        }
                    }

                    var existingClient = await _dbContext.Clients
                        .FirstOrDefaultAsync(c => c.ClientId == clientDef.ClientId, cancellationToken);

                    if (existingClient != null)
                    {
                        var resolution = options.GetResolution("Client", clientDef.ClientId);

                        switch (resolution)
                        {
                            case ConflictResolution.Skip:
                                skippedCount++;
                                _logger.LogInformation("Skipped existing client {ClientId}", clientDef.ClientId);
                                continue;

                            case ConflictResolution.Overwrite:
                                try
                                {
                                    await UpdateClientAsync(existingClient, clientDef, options, cancellationToken);
                                    updatedCount++;
                                    _logger.LogInformation("Updated existing client {ClientId}", clientDef.ClientId);
                                }
                                catch (ImportValidationException ex)
                                {
                                    errors.Add(ex.Error);
                                    skippedCount++;
                                    _logger.LogWarning("Skipped updating client {ClientId}: {Reason}", clientDef.ClientId, ex.Message);
                                }
                                break;

                            case ConflictResolution.Merge:
                                try
                                {
                                    await MergeClientAsync(existingClient, clientDef, options, cancellationToken);
                                    updatedCount++;
                                    _logger.LogInformation("Merged client {ClientId}", clientDef.ClientId);
                                }
                                catch (ImportValidationException ex)
                                {
                                    errors.Add(ex.Error);
                                    skippedCount++;
                                    _logger.LogWarning("Skipped merging client {ClientId}: {Reason}", clientDef.ClientId, ex.Message);
                                }
                                break;

                            case ConflictResolution.Rename:
                                var newClientId = await GenerateUniqueClientIdAsync(clientDef.ClientId, cancellationToken);
                                try
                                {
                                    await CreateClientAsync(realmId, clientDef with { ClientId = newClientId }, options, cancellationToken);
                                    createdCount++;
                                    _logger.LogInformation("Created renamed client {NewClientId} (was {OldClientId})", newClientId, clientDef.ClientId);
                                }
                                catch (ImportValidationException ex)
                                {
                                    errors.Add(ex.Error);
                                    skippedCount++;
                                    _logger.LogWarning("Skipped creating renamed client {NewClientId} (was {OldClientId}): {Reason}", newClientId, clientDef.ClientId, ex.Message);
                                }
                                break;
                        }
                    }
                    else
                    {
                        try
                        {
                            await CreateClientAsync(realmId, clientDef, options, cancellationToken);
                            createdCount++;
                            _logger.LogInformation("Created client {ClientId}", clientDef.ClientId);
                        }
                        catch (ImportValidationException ex)
                        {
                            errors.Add(ex.Error);
                            skippedCount++;
                            _logger.LogWarning("Skipped creating client {ClientId}: {Reason}", clientDef.ClientId, ex.Message);
                        }
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            });

            await LogImportAuditAsync(
                tenantId, "Client", clients.FirstOrDefault().Client?.ClientId ?? "unknown",
                options, true, createdCount, updatedCount, skippedCount, null, manifest.Metadata?.Checksum,
                cancellationToken);

            return new ImportResult
            {
                Success = true,
                EntitiesCreated = createdCount,
                EntitiesUpdated = updatedCount,
                EntitiesSkipped = skippedCount,
                Errors = errors,
                Warnings = errors.Count > 0 ? errors.Select(e => $"{e.Identifier}: {e.Message}").ToList() : []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import clients");

            await LogImportAuditAsync(
                tenantId, "Client", clients.FirstOrDefault().Client?.ClientId ?? "unknown",
                options, false, createdCount, updatedCount, skippedCount, ex.Message, manifest.Metadata?.Checksum,
                cancellationToken);

            return ImportResult.Failed(new ImportError
            {
                EntityType = "Client",
                Code = "IMPORT_FAILED",
                Message = ex.Message
            });
        }
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportIdentityProviderAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Require target tenant ID for standalone provider imports
        if (!options.TargetTenantId.HasValue)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "IdentityProvider",
                Code = "MISSING_TARGET_TENANT",
                Message = "Target tenant ID must be specified when importing identity providers"
            });
        }

        var tenantId = options.TargetTenantId.Value;

        // Verify tenant exists
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant == null)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "IdentityProvider",
                Code = "TENANT_NOT_FOUND",
                Message = $"Target tenant with ID '{tenantId}' was not found"
            });
        }

        // Collect providers from both standalone and tenant-nested sources
        var providers = new List<IdentityProviderSeedDefinition>();
        providers.AddRange(manifest.Data?.IdentityProviders ?? []);
        foreach (var tenantDef in manifest.Data?.Tenants ?? [])
        {
            providers.AddRange(tenantDef.IdentityProviders ?? []);
        }

        if (providers.Count == 0)
        {
            return ImportResult.Failed(new ImportError
            {
                EntityType = "IdentityProvider",
                Code = "NO_PROVIDERS",
                Message = "No identity providers found in the manifest"
            });
        }

        if (options.ValidateOnly)
        {
            // Dry run - just count what would be created/updated/skipped
            var (created, updated, skipped) = await CountProviderOperationsAsync(tenantId, providers, options, cancellationToken);
            return new ImportResult
            {
                Success = true,
                EntitiesCreated = created,
                EntitiesUpdated = updated,
                EntitiesSkipped = skipped
            };
        }

        var createdCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var errors = new List<ImportError>();

        var strategy = _dbContext.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                foreach (var providerDef in providers)
                {
                    var existingProvider = await _dbContext.IdentityProviders
                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Name == providerDef.Name, cancellationToken);

                    if (existingProvider != null)
                    {
                        var resolution = options.GetResolution("IdentityProvider", providerDef.Name);

                        switch (resolution)
                        {
                            case ConflictResolution.Skip:
                                skippedCount++;
                                _logger.LogInformation("Skipped existing provider {Name}", providerDef.Name);
                                continue;

                            case ConflictResolution.Overwrite:
                                await UpdateProviderAsync(existingProvider, providerDef, cancellationToken);
                                updatedCount++;
                                _logger.LogInformation("Updated existing provider {Name}", providerDef.Name);
                                break;

                            case ConflictResolution.Merge:
                                await MergeProviderAsync(existingProvider, providerDef, cancellationToken);
                                updatedCount++;
                                _logger.LogInformation("Merged provider {Name}", providerDef.Name);
                                break;

                            case ConflictResolution.Rename:
                                var newName = await GenerateUniqueProviderNameAsync(tenantId, providerDef.Name, cancellationToken);
                                await CreateProviderAsync(tenantId, providerDef with { Name = newName }, cancellationToken);
                                createdCount++;
                                _logger.LogInformation("Created renamed provider {NewName} (was {OldName})", newName, providerDef.Name);
                                break;
                        }
                    }
                    else
                    {
                        await CreateProviderAsync(tenantId, providerDef, cancellationToken);
                        createdCount++;
                        _logger.LogInformation("Created provider {Name}", providerDef.Name);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            });

            // Log audit
            await LogImportAuditAsync(
                tenantId, "IdentityProvider", providers.FirstOrDefault()?.Name ?? "unknown",
                options, true, createdCount, updatedCount, skippedCount, null, manifest.Metadata?.Checksum,
                cancellationToken);

            return new ImportResult
            {
                Success = true,
                EntitiesCreated = createdCount,
                EntitiesUpdated = updatedCount,
                EntitiesSkipped = skippedCount,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import identity providers");

            await LogImportAuditAsync(
                tenantId, "IdentityProvider", providers.FirstOrDefault()?.Name ?? "unknown",
                options, false, createdCount, updatedCount, skippedCount, ex.Message, manifest.Metadata?.Checksum,
                cancellationToken);

            return ImportResult.Failed(new ImportError
            {
                EntityType = "IdentityProvider",
                Code = "IMPORT_FAILED",
                Message = ex.Message
            });
        }
    }

    private async Task<(int created, int updated, int skipped)> CountProviderOperationsAsync(
        Guid tenantId,
        List<IdentityProviderSeedDefinition> providers,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var providerDef in providers)
        {
            var exists = await _dbContext.IdentityProviders
                .AnyAsync(p => p.TenantId == tenantId && p.Name == providerDef.Name, cancellationToken);

            if (exists)
            {
                var resolution = options.GetResolution("IdentityProvider", providerDef.Name);
                switch (resolution)
                {
                    case ConflictResolution.Skip:
                        skipped++;
                        break;
                    case ConflictResolution.Rename:
                        created++;
                        break;
                    default: // Merge, Overwrite
                        updated++;
                        break;
                }
            }
            else
            {
                created++;
            }
        }

        return (created, updated, skipped);
    }

    private async Task CreateProviderAsync(
        Guid tenantId,
        IdentityProviderSeedDefinition providerDef,
        CancellationToken cancellationToken)
    {
        var provider = new IdentityProvider
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            Name = providerDef.Name,
            DisplayName = providerDef.DisplayName,
            Type = Enum.TryParse<IdentityProviderType>(providerDef.Type, true, out var type) ? type : IdentityProviderType.Oidc,
            Enabled = providerDef.Enabled ?? true,
            IsDefault = providerDef.IsDefault ?? false,
            AllowRegistration = providerDef.AllowRegistration ?? false,
            LogoUrl = providerDef.LogoUrl,
            SortOrder = providerDef.SortOrder ?? 0,
            ConfigJson = providerDef.Config != null ? JsonSerializer.Serialize(providerDef.Config) : null
        };
        _dbContext.IdentityProviders.Add(provider);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Add claim mappings
        foreach (var mappingDef in providerDef.ClaimMappings ?? [])
        {
            var mapping = new IdentityProviderClaimMapping
            {
                Id = GuidHelper.NewId(),
                IdentityProviderId = provider.Id,
                ExternalClaim = mappingDef.ExternalClaim,
                LocalClaim = mappingDef.LocalClaim,
                Transform = mappingDef.Transform,
                Order = mappingDef.Order ?? 0
            };
            _dbContext.IdentityProviderClaimMappings.Add(mapping);
        }

        // Add keys
        foreach (var keyDef in providerDef.Keys ?? [])
        {
            var key = new IdentityProviderKey
            {
                Id = GuidHelper.NewId(),
                IdentityProviderId = provider.Id,
                Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(keyDef.Purpose, true, out var purpose) ? purpose : IdentityProviderKeyPurpose.Signing,
                Alg = keyDef.Alg,
                Kid = keyDef.Kid,
                Jwk = keyDef.Jwk ?? string.Empty,
                Active = keyDef.Active ?? true
            };
            _dbContext.IdentityProviderKeys.Add(key);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateProviderAsync(
        IdentityProvider provider,
        IdentityProviderSeedDefinition providerDef,
        CancellationToken cancellationToken)
    {
        // Update all properties
        provider.DisplayName = providerDef.DisplayName;
        provider.Type = Enum.TryParse<IdentityProviderType>(providerDef.Type, true, out var type) ? type : IdentityProviderType.Oidc;
        provider.Enabled = providerDef.Enabled ?? true;
        provider.IsDefault = providerDef.IsDefault ?? false;
        provider.AllowRegistration = providerDef.AllowRegistration ?? false;
        provider.LogoUrl = providerDef.LogoUrl;
        provider.SortOrder = providerDef.SortOrder ?? 0;
        provider.ConfigJson = providerDef.Config != null ? JsonSerializer.Serialize(providerDef.Config) : null;

        // Remove existing claim mappings and keys
        var existingMappings = await _dbContext.IdentityProviderClaimMappings
            .Where(m => m.IdentityProviderId == provider.Id)
            .ToListAsync(cancellationToken);
        _dbContext.IdentityProviderClaimMappings.RemoveRange(existingMappings);

        var existingKeys = await _dbContext.IdentityProviderKeys
            .Where(k => k.IdentityProviderId == provider.Id)
            .ToListAsync(cancellationToken);
        _dbContext.IdentityProviderKeys.RemoveRange(existingKeys);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Add new claim mappings
        foreach (var mappingDef in providerDef.ClaimMappings ?? [])
        {
            var mapping = new IdentityProviderClaimMapping
            {
                Id = GuidHelper.NewId(),
                IdentityProviderId = provider.Id,
                ExternalClaim = mappingDef.ExternalClaim,
                LocalClaim = mappingDef.LocalClaim,
                Transform = mappingDef.Transform,
                Order = mappingDef.Order ?? 0
            };
            _dbContext.IdentityProviderClaimMappings.Add(mapping);
        }

        // Add new keys
        foreach (var keyDef in providerDef.Keys ?? [])
        {
            var key = new IdentityProviderKey
            {
                Id = GuidHelper.NewId(),
                IdentityProviderId = provider.Id,
                Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(keyDef.Purpose, true, out var purpose) ? purpose : IdentityProviderKeyPurpose.Signing,
                Alg = keyDef.Alg,
                Kid = keyDef.Kid,
                Jwk = keyDef.Jwk ?? string.Empty,
                Active = keyDef.Active ?? true
            };
            _dbContext.IdentityProviderKeys.Add(key);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MergeProviderAsync(
        IdentityProvider provider,
        IdentityProviderSeedDefinition providerDef,
        CancellationToken cancellationToken)
    {
        // Merge: only update non-null/non-default values
        if (providerDef.DisplayName != null)
            provider.DisplayName = providerDef.DisplayName;
        if (providerDef.Type != null && Enum.TryParse<IdentityProviderType>(providerDef.Type, true, out var type))
            provider.Type = type;
        if (providerDef.Enabled.HasValue)
            provider.Enabled = providerDef.Enabled.Value;
        if (providerDef.IsDefault.HasValue)
            provider.IsDefault = providerDef.IsDefault.Value;
        if (providerDef.AllowRegistration.HasValue)
            provider.AllowRegistration = providerDef.AllowRegistration.Value;
        if (providerDef.LogoUrl != null)
            provider.LogoUrl = providerDef.LogoUrl;
        if (providerDef.SortOrder.HasValue)
            provider.SortOrder = providerDef.SortOrder.Value;
        if (providerDef.Config != null)
            provider.ConfigJson = JsonSerializer.Serialize(providerDef.Config);

        // Merge claim mappings (add new ones, don't remove existing)
        var existingMappings = await _dbContext.IdentityProviderClaimMappings
            .Where(m => m.IdentityProviderId == provider.Id)
            .ToListAsync(cancellationToken);

        foreach (var mappingDef in providerDef.ClaimMappings ?? [])
        {
            var existing = existingMappings.FirstOrDefault(m => m.ExternalClaim == mappingDef.ExternalClaim);
            if (existing == null)
            {
                var mapping = new IdentityProviderClaimMapping
                {
                    Id = GuidHelper.NewId(),
                    IdentityProviderId = provider.Id,
                    ExternalClaim = mappingDef.ExternalClaim,
                    LocalClaim = mappingDef.LocalClaim,
                    Transform = mappingDef.Transform,
                    Order = mappingDef.Order ?? 0
                };
                _dbContext.IdentityProviderClaimMappings.Add(mapping);
            }
            else
            {
                // Update existing mapping
                existing.LocalClaim = mappingDef.LocalClaim;
                existing.Transform = mappingDef.Transform;
                existing.Order = mappingDef.Order ?? existing.Order;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueProviderNameAsync(Guid tenantId, string baseName, CancellationToken cancellationToken)
    {
        var suffix = 1;
        var candidate = $"{baseName}_imported";

        while (await _dbContext.IdentityProviders.AnyAsync(p => p.TenantId == tenantId && p.Name == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseName}_imported_{suffix}";
        }

        return candidate;
    }

    // ========== Realm Helper Methods ==========

    private async Task<(int created, int updated, int skipped)> CountRealmOperationsAsync(
        Guid tenantId,
        List<RealmSeedDefinition> realms,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var realmDef in realms)
        {
            var exists = await _dbContext.Realms
                .AnyAsync(r => r.TenantId == tenantId && r.Name == realmDef.Name, cancellationToken);

            if (exists)
            {
                var resolution = options.GetResolution("Realm", realmDef.Name);
                switch (resolution)
                {
                    case ConflictResolution.Skip:
                        skipped++;
                        break;
                    case ConflictResolution.Rename:
                        created++;
                        break;
                    default:
                        updated++;
                        break;
                }
            }
            else
            {
                created++;
            }
        }

        return (created, updated, skipped);
    }

    private async Task CreateRealmAsync(
        Guid tenantId,
        RealmSeedDefinition realmDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var realm = new Realm
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            Name = realmDef.Name,
            DisplayName = realmDef.DisplayName,
            AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin ?? false
        };
        _dbContext.Realms.Add(realm);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Import clients within this realm
        foreach (var clientDef in realmDef.Clients ?? [])
        {
            await CreateClientAsync(realm.Id, clientDef, options, cancellationToken);
        }

        // Import roles within this realm
        foreach (var roleDef in realmDef.Roles ?? [])
        {
            var role = new Role
            {
                Id = GuidHelper.NewId(),
                TenantId = tenantId,
                RealmId = realm.Id,
                Name = roleDef.Name,
                IsActive = roleDef.IsActive ?? true
            };
            _dbContext.Roles.Add(role);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateRealmAsync(
        Realm realm,
        RealmSeedDefinition realmDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        realm.DisplayName = realmDef.DisplayName;
        realm.AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin ?? realm.AllowUnconfirmedLogin;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MergeRealmAsync(
        Realm realm,
        RealmSeedDefinition realmDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        if (realmDef.DisplayName != null)
            realm.DisplayName = realmDef.DisplayName;
        if (realmDef.AllowUnconfirmedLogin.HasValue)
            realm.AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin.Value;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueRealmNameAsync(Guid tenantId, string baseName, CancellationToken cancellationToken)
    {
        var suffix = 1;
        var candidate = $"{baseName}_imported";

        while (await _dbContext.Realms.AnyAsync(r => r.TenantId == tenantId && r.Name == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseName}_imported_{suffix}";
        }

        return candidate;
    }

    // ========== Client Helper Methods ==========

    private async Task<(int created, int updated, int skipped)> CountClientOperationsAsync(
        Guid tenantId,
        List<(ClientSeedDefinition Client, string? RealmName)> clients,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var (clientDef, _) in clients)
        {
            var exists = await _dbContext.Clients
                .AnyAsync(c => c.ClientId == clientDef.ClientId, cancellationToken);

            if (exists)
            {
                var resolution = options.GetResolution("Client", clientDef.ClientId);
                switch (resolution)
                {
                    case ConflictResolution.Skip:
                        skipped++;
                        break;
                    case ConflictResolution.Rename:
                        created++;
                        break;
                    default:
                        updated++;
                        break;
                }
            }
            else
            {
                created++;
            }
        }

        return (created, updated, skipped);
    }

    private async Task CreateClientAsync(
        Guid realmId,
        ClientSeedDefinition clientDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        // Look up tenant from realm
        var realm = await _dbContext.Realms.FirstOrDefaultAsync(r => r.Id == realmId, cancellationToken);
        var tenantId = realm?.TenantId ?? Guid.Empty;

        var allowedScopes = NormalizeScopeNames(clientDef.AllowedScopes);
        await EnsureScopesExistAsync(tenantId, allowedScopes, cancellationToken);

        var client = new Client
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            RealmId = realmId,
            ClientId = clientDef.ClientId,
            ClientName = clientDef.ClientName,
            RequirePkce = clientDef.RequirePkce ?? true,
            RequireConsent = clientDef.RequireConsent ?? false,
            RequirePar = clientDef.RequirePar ?? false,
            AutoApprovalMode = Enum.TryParse<AutoApprovalMode>(clientDef.AutoApprovalMode, true, out var mode) ? mode : AutoApprovalMode.No,
            AllowLocalLogin = clientDef.AllowLocalLogin ?? true,
            AllowExternalIdp = clientDef.AllowExternalIdp ?? true,
            AllowQrLogin = clientDef.AllowQrLogin ?? false,
            BackChannelLogoutUri = clientDef.BackChannelLogoutUri,
            FrontChannelLogoutUri = clientDef.FrontChannelLogoutUri,
            PublicJwksJson = clientDef.PublicJwksJson,
            PublicJwksUri = clientDef.PublicJwksUri,
            // Redirect URIs stored as JSON
            AllowedLoginRedirectUrisJson = clientDef.AllowedLoginRedirectUris?.Count > 0
                ? JsonSerializer.Serialize(clientDef.AllowedLoginRedirectUris)
                : null,
            AllowedLogoutRedirectUrisJson = clientDef.AllowedLogoutRedirectUris?.Count > 0
                ? JsonSerializer.Serialize(clientDef.AllowedLogoutRedirectUris)
                : null
        };

        // Handle client secret using new ClientSecrets collection
        if (!string.IsNullOrEmpty(clientDef.ClientSecretHash) && !ExportManifest.IsObfuscated(clientDef.ClientSecretHash))
        {
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = clientDef.ClientSecretHash,
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }
        else if (!string.IsNullOrEmpty(clientDef.ClientSecret))
        {
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = BCrypt.Net.BCrypt.HashPassword(clientDef.ClientSecret),
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }
        else if (options.Secrets.TryGetValue(clientDef.ClientId, out var secret))
        {
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = BCrypt.Net.BCrypt.HashPassword(secret),
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }

        _dbContext.Clients.Add(client);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Add scopes
        foreach (var scopeName in allowedScopes)
        {
            _dbContext.ClientScopes.Add(new ClientScope
            {
                ClientId = client.Id,
                ScopeName = scopeName
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateClientAsync(
        Client client,
        ClientSeedDefinition clientDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        client.ClientName = clientDef.ClientName;
        client.RequirePkce = clientDef.RequirePkce ?? client.RequirePkce;
        client.RequireConsent = clientDef.RequireConsent ?? client.RequireConsent;
        client.RequirePar = clientDef.RequirePar ?? client.RequirePar;
        if (clientDef.AutoApprovalMode != null && Enum.TryParse<AutoApprovalMode>(clientDef.AutoApprovalMode, true, out var mode))
            client.AutoApprovalMode = mode;
        client.AllowLocalLogin = clientDef.AllowLocalLogin ?? client.AllowLocalLogin;
        client.AllowExternalIdp = clientDef.AllowExternalIdp ?? client.AllowExternalIdp;
        client.AllowQrLogin = clientDef.AllowQrLogin ?? client.AllowQrLogin;
        client.BackChannelLogoutUri = clientDef.BackChannelLogoutUri;
        client.FrontChannelLogoutUri = clientDef.FrontChannelLogoutUri;
        client.PublicJwksJson = clientDef.PublicJwksJson;
        client.PublicJwksUri = clientDef.PublicJwksUri;

        // Update redirect URIs (JSON fields)
        if (clientDef.AllowedLoginRedirectUris?.Count > 0)
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(clientDef.AllowedLoginRedirectUris);
        if (clientDef.AllowedLogoutRedirectUris?.Count > 0)
            client.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(clientDef.AllowedLogoutRedirectUris);

        // Handle secret update using ClientSecrets collection
        if (!string.IsNullOrEmpty(clientDef.ClientSecretHash) && !ExportManifest.IsObfuscated(clientDef.ClientSecretHash))
        {
            // Mark existing secrets as non-primary, add new one
            foreach (var existingSecret in client.ClientSecrets.Where(s => s.IsPrimary))
                existingSecret.IsPrimary = false;
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = clientDef.ClientSecretHash,
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }
        else if (!string.IsNullOrEmpty(clientDef.ClientSecret))
        {
            foreach (var existingSecret in client.ClientSecrets.Where(s => s.IsPrimary))
                existingSecret.IsPrimary = false;
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = BCrypt.Net.BCrypt.HashPassword(clientDef.ClientSecret),
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }
        else if (options.Secrets.TryGetValue(clientDef.ClientId, out var secret))
        {
            foreach (var existingSecret in client.ClientSecrets.Where(s => s.IsPrimary))
                existingSecret.IsPrimary = false;
            client.ClientSecrets.Add(new ClientSecret
            {
                Id = GuidHelper.NewId(),
                ClientId = client.Id,
                SecretHash = BCrypt.Net.BCrypt.HashPassword(secret),
                IsPrimary = true,
                CreatedAtUtc = DateTime.UtcNow,
                ActivatedAtUtc = DateTime.UtcNow
            });
        }

        var allowedScopes = NormalizeScopeNames(clientDef.AllowedScopes);
        await EnsureScopesExistAsync(client.TenantId, allowedScopes, cancellationToken);

        // Replace scopes
        var existingScopes = await _dbContext.ClientScopes
            .Where(s => s.ClientId == client.Id)
            .ToListAsync(cancellationToken);
        _dbContext.ClientScopes.RemoveRange(existingScopes);

        foreach (var scopeName in allowedScopes)
        {
            _dbContext.ClientScopes.Add(new ClientScope
            {
                ClientId = client.Id,
                ScopeName = scopeName
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MergeClientAsync(
        Client client,
        ClientSeedDefinition clientDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        if (clientDef.ClientName != null)
            client.ClientName = clientDef.ClientName;
        if (clientDef.RequirePkce.HasValue)
            client.RequirePkce = clientDef.RequirePkce.Value;
        if (clientDef.RequireConsent.HasValue)
            client.RequireConsent = clientDef.RequireConsent.Value;
        if (clientDef.RequirePar.HasValue)
            client.RequirePar = clientDef.RequirePar.Value;
        if (clientDef.AutoApprovalMode != null && Enum.TryParse<AutoApprovalMode>(clientDef.AutoApprovalMode, true, out var mode))
            client.AutoApprovalMode = mode;
        if (clientDef.AllowLocalLogin.HasValue)
            client.AllowLocalLogin = clientDef.AllowLocalLogin.Value;
        if (clientDef.AllowExternalIdp.HasValue)
            client.AllowExternalIdp = clientDef.AllowExternalIdp.Value;
        if (clientDef.AllowQrLogin.HasValue)
            client.AllowQrLogin = clientDef.AllowQrLogin.Value;
        if (clientDef.BackChannelLogoutUri != null)
            client.BackChannelLogoutUri = clientDef.BackChannelLogoutUri;
        if (clientDef.FrontChannelLogoutUri != null)
            client.FrontChannelLogoutUri = clientDef.FrontChannelLogoutUri;

        // Merge redirect URIs (combine existing with new)
        if (clientDef.AllowedLoginRedirectUris?.Count > 0)
        {
            var existing = !string.IsNullOrEmpty(client.AllowedLoginRedirectUrisJson)
                ? JsonSerializer.Deserialize<List<string>>(client.AllowedLoginRedirectUrisJson) ?? []
                : new List<string>();
            var merged = existing.Union(clientDef.AllowedLoginRedirectUris).Distinct().ToList();
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(merged);
        }
        if (clientDef.AllowedLogoutRedirectUris?.Count > 0)
        {
            var existing = !string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson)
                ? JsonSerializer.Deserialize<List<string>>(client.AllowedLogoutRedirectUrisJson) ?? []
                : new List<string>();
            var merged = existing.Union(clientDef.AllowedLogoutRedirectUris).Distinct().ToList();
            client.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(merged);
        }

        // Merge scopes (add new ones)
        var existingScopes = await _dbContext.ClientScopes
            .Where(s => s.ClientId == client.Id)
            .Select(s => s.ScopeName)
            .ToListAsync(cancellationToken);

        var desiredScopes = NormalizeScopeNames(clientDef.AllowedScopes);
        await EnsureScopesExistAsync(client.TenantId, desiredScopes, cancellationToken);

        foreach (var scopeName in desiredScopes)
        {
            if (!existingScopes.Contains(scopeName))
            {
                _dbContext.ClientScopes.Add(new ClientScope
                {
                    ClientId = client.Id,
                    ScopeName = scopeName
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<ValidationError> ValidateManifest(ExportManifest manifest)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrEmpty(manifest.Schema))
        {
            errors.Add(new ValidationError
            {
                Path = "schema",
                Code = "MISSING_SCHEMA",
                Message = "Manifest schema is required",
                Severity = ValidationSeverity.Error
            });
        }

        if (manifest.Version <= 0)
        {
            errors.Add(new ValidationError
            {
                Path = "version",
                Code = "INVALID_VERSION",
                Message = "Manifest version must be a positive integer",
                Severity = ValidationSeverity.Error
            });
        }

        if (manifest.Data == null)
        {
            errors.Add(new ValidationError
            {
                Path = "data",
                Code = "MISSING_DATA",
                Message = "Manifest data is required",
                Severity = ValidationSeverity.Error
            });
        }

        return errors;
    }

    private async Task<List<ImportConflict>> DetectConflictsAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<ImportConflict>();

        foreach (var tenantDef in manifest.Data?.Tenants ?? [])
        {
            var existingTenant = await _dbContext.Tenants
                .FirstOrDefaultAsync(t => t.Slug == tenantDef.Slug, cancellationToken);

            if (existingTenant != null)
            {
                conflicts.Add(new ImportConflict
                {
                    Type = ConflictType.TenantSlugExists,
                    EntityType = "Tenant",
                    Identifier = tenantDef.Slug,
                    ExistingEntityId = existingTenant.Id,
                    SuggestedRename = await GenerateUniqueTenantSlugAsync(tenantDef.Slug, cancellationToken),
                    Resolution = options.GetResolution("Tenant", tenantDef.Slug)
                });
            }

            // Check for realm conflicts within tenant
            foreach (var realmDef in tenantDef.Realms ?? [])
            {
                if (existingTenant != null)
                {
                    var existingRealm = await _dbContext.Realms
                        .FirstOrDefaultAsync(r => r.TenantId == existingTenant.Id && r.Name == realmDef.Name, cancellationToken);

                    if (existingRealm != null)
                    {
                        conflicts.Add(new ImportConflict
                        {
                            Type = ConflictType.RealmNameExists,
                            EntityType = "Realm",
                            Identifier = $"{tenantDef.Slug}/{realmDef.Name}",
                            ExistingEntityId = existingRealm.Id,
                            SuggestedRename = $"{realmDef.Name}_imported",
                            Resolution = options.GetResolution("Realm", realmDef.Name)
                        });
                    }
                }
            }

            // Check for client conflicts within tenant
            foreach (var clientDef in tenantDef.Clients ?? [])
            {
                var existingClient = await _dbContext.Clients
                    .FirstOrDefaultAsync(c => c.ClientId == clientDef.ClientId, cancellationToken);

                if (existingClient != null)
                {
                    conflicts.Add(new ImportConflict
                    {
                        Type = ConflictType.ClientIdExists,
                        EntityType = "Client",
                        Identifier = clientDef.ClientId,
                        ExistingEntityId = existingClient.Id,
                        SuggestedRename = await GenerateUniqueClientIdAsync(clientDef.ClientId, cancellationToken),
                        Resolution = options.GetResolution("Client", clientDef.ClientId)
                    });
                }
            }

            // Check for identity provider conflicts
            foreach (var providerDef in tenantDef.IdentityProviders ?? [])
            {
                if (existingTenant != null)
                {
                    var existingProvider = await _dbContext.IdentityProviders
                        .FirstOrDefaultAsync(p => p.TenantId == existingTenant.Id && p.Name == providerDef.Name, cancellationToken);

                    if (existingProvider != null)
                    {
                        conflicts.Add(new ImportConflict
                        {
                            Type = ConflictType.ProviderNameExists,
                            EntityType = "IdentityProvider",
                            Identifier = $"{tenantDef.Slug}/{providerDef.Name}",
                            ExistingEntityId = existingProvider.Id,
                            SuggestedRename = $"{providerDef.Name}_imported",
                            Resolution = options.GetResolution("IdentityProvider", providerDef.Name)
                        });
                    }
                }
            }
        }

        return conflicts;
    }

    private static (List<EntitySummary> ToCreate, List<EntitySummary> ToUpdate) BuildEntityLists(
        ExportManifest manifest,
        List<ImportConflict> conflicts,
        ImportOptions options)
    {
        var toCreate = new List<EntitySummary>();
        var toUpdate = new List<EntitySummary>();

        var conflictLookup = conflicts.ToDictionary(c => c.Identifier);

        foreach (var tenantDef in manifest.Data?.Tenants ?? [])
        {
            if (conflictLookup.TryGetValue(tenantDef.Slug, out var conflict))
            {
                var resolution = conflict.Resolution ?? options.GetResolution("Tenant", tenantDef.Slug);
                switch (resolution)
                {
                    case ConflictResolution.Overwrite:
                    case ConflictResolution.Merge:
                        toUpdate.Add(new EntitySummary { Type = "Tenant", Identifier = tenantDef.Slug, DisplayName = tenantDef.Name });
                        break;
                    case ConflictResolution.Rename:
                        toCreate.Add(new EntitySummary { Type = "Tenant", Identifier = conflict.SuggestedRename!, DisplayName = tenantDef.Name });
                        break;
                        // Skip: don't add to either list
                }
            }
            else
            {
                toCreate.Add(new EntitySummary { Type = "Tenant", Identifier = tenantDef.Slug, DisplayName = tenantDef.Name });
            }

            // Count nested entities
            foreach (var realmDef in tenantDef.Realms ?? [])
            {
                var realmKey = $"{tenantDef.Slug}/{realmDef.Name}";
                if (!conflictLookup.ContainsKey(realmKey))
                {
                    toCreate.Add(new EntitySummary { Type = "Realm", Identifier = realmDef.Name, DisplayName = realmDef.DisplayName });
                }
            }

            foreach (var clientDef in tenantDef.Clients ?? [])
            {
                if (!conflictLookup.ContainsKey(clientDef.ClientId))
                {
                    toCreate.Add(new EntitySummary { Type = "Client", Identifier = clientDef.ClientId, DisplayName = clientDef.ClientName });
                }
            }

            foreach (var providerDef in tenantDef.IdentityProviders ?? [])
            {
                var providerKey = $"{tenantDef.Slug}/{providerDef.Name}";
                if (!conflictLookup.ContainsKey(providerKey))
                {
                    toCreate.Add(new EntitySummary { Type = "IdentityProvider", Identifier = providerDef.Name, DisplayName = providerDef.DisplayName });
                }
            }
        }

        // Handle standalone identity providers
        foreach (var providerDef in manifest.Data?.IdentityProviders ?? [])
        {
            if (!conflictLookup.ContainsKey(providerDef.Name))
            {
                toCreate.Add(new EntitySummary { Type = "IdentityProvider", Identifier = providerDef.Name, DisplayName = providerDef.DisplayName });
            }
        }

        return (toCreate, toUpdate);
    }

    private static Tenant CreateTenant(TenantSeedDefinition def)
    {
        return new Tenant
        {
            Id = GuidHelper.NewId(),
            Slug = def.Slug,
            Name = def.Name,
            Description = def.Description,
            IssuerUri = def.IssuerUri ?? $"https://{def.Slug}.example.com",
            AdminEmail = def.AdminEmail,
            BillingPlan = def.BillingPlan,
            Status = Enum.TryParse<TenantStatus>(def.Status, out var status) ? status : TenantStatus.Active,
            LogoUrl = def.LogoUrl,
            PrimaryColor = def.PrimaryColor,
            AccentColor = def.AccentColor,
            SettingsJson = def.SettingsJson,
            MaxUsers = def.MaxUsers ?? 0,
            MaxClients = def.MaxClients ?? 0
        };
    }

    private static void UpdateTenant(Tenant tenant, TenantSeedDefinition def)
    {
        tenant.Name = def.Name;
        tenant.Description = def.Description;
        tenant.IssuerUri = def.IssuerUri ?? tenant.IssuerUri;
        tenant.AdminEmail = def.AdminEmail;
        tenant.BillingPlan = def.BillingPlan;
        tenant.Status = Enum.TryParse<TenantStatus>(def.Status, out var status) ? status : tenant.Status;
        tenant.LogoUrl = def.LogoUrl;
        tenant.PrimaryColor = def.PrimaryColor;
        tenant.AccentColor = def.AccentColor;
        tenant.SettingsJson = def.SettingsJson;
        tenant.MaxUsers = def.MaxUsers ?? tenant.MaxUsers;
        tenant.MaxClients = def.MaxClients ?? tenant.MaxClients;
    }

    private static void MergeTenant(Tenant tenant, TenantSeedDefinition def)
    {
        if (!string.IsNullOrEmpty(def.Name)) tenant.Name = def.Name;
        if (!string.IsNullOrEmpty(def.Description)) tenant.Description = def.Description;
        if (!string.IsNullOrEmpty(def.IssuerUri)) tenant.IssuerUri = def.IssuerUri;
        if (!string.IsNullOrEmpty(def.AdminEmail)) tenant.AdminEmail = def.AdminEmail;
        if (!string.IsNullOrEmpty(def.BillingPlan)) tenant.BillingPlan = def.BillingPlan;
        if (!string.IsNullOrEmpty(def.Status) && Enum.TryParse<TenantStatus>(def.Status, out var status))
            tenant.Status = status;
        if (!string.IsNullOrEmpty(def.LogoUrl)) tenant.LogoUrl = def.LogoUrl;
        if (!string.IsNullOrEmpty(def.PrimaryColor)) tenant.PrimaryColor = def.PrimaryColor;
        if (!string.IsNullOrEmpty(def.AccentColor)) tenant.AccentColor = def.AccentColor;
        if (!string.IsNullOrEmpty(def.SettingsJson)) tenant.SettingsJson = def.SettingsJson;
        if (def.MaxUsers.HasValue) tenant.MaxUsers = def.MaxUsers.Value;
        if (def.MaxClients.HasValue) tenant.MaxClients = def.MaxClients.Value;
    }

    private async Task ImportRealmsAsync(
        Guid tenantId,
        List<RealmSeedDefinition> realms,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var realmDef in realms)
        {
            var realm = new Realm
            {
                Id = GuidHelper.NewId(),
                TenantId = tenantId,
                Name = realmDef.Name,
                DisplayName = realmDef.DisplayName,
                AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin ?? false
            };
            _dbContext.Realms.Add(realm);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportClientsAsync(
        Guid tenantId,
        TenantSeedDefinition tenantDef,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var realms = await _dbContext.Realms
            .Where(r => r.TenantId == tenantId)
            .ToDictionaryAsync(r => r.Name, r => r.Id, cancellationToken);

        foreach (var clientDef in tenantDef.Clients ?? [])
        {
            if (!realms.TryGetValue(clientDef.Realm ?? "admin", out var realmId))
            {
                _logger.LogWarning("Realm {Realm} not found for client {ClientId}, skipping", clientDef.Realm, clientDef.ClientId);
                continue;
            }

            var client = new Client
            {
                Id = GuidHelper.NewId(),
                TenantId = tenantId,
                RealmId = realmId,
                ClientId = clientDef.ClientId,
                ClientName = clientDef.ClientName,
                RequirePkce = clientDef.RequirePkce ?? true,
                RequireConsent = clientDef.RequireConsent ?? true,
                RequirePar = clientDef.RequirePar ?? false,
                AutoApprovalMode = Enum.TryParse<AutoApprovalMode>(clientDef.AutoApprovalMode, out var mode) ? mode : AutoApprovalMode.No,
                AllowedLoginRedirectUrisJson = clientDef.AllowedLoginRedirectUris != null ? JsonSerializer.Serialize(clientDef.AllowedLoginRedirectUris) : null,
                AllowedLogoutRedirectUrisJson = clientDef.AllowedLogoutRedirectUris != null ? JsonSerializer.Serialize(clientDef.AllowedLogoutRedirectUris) : null,
                AllowLocalLogin = clientDef.AllowLocalLogin ?? true,
                AllowExternalIdp = clientDef.AllowExternalIdp ?? true,
                AllowQrLogin = clientDef.AllowQrLogin ?? false,
                BackChannelLogoutUri = clientDef.BackChannelLogoutUri,
                BackChannelLogoutSessionRequired = clientDef.BackChannelLogoutSessionRequired ?? true,
                FrontChannelLogoutUri = clientDef.FrontChannelLogoutUri,
                FrontChannelLogoutSessionRequired = clientDef.FrontChannelLogoutSessionRequired ?? true,
                OboEnabled = clientDef.OboEnabled,
                OboAllowedSourceAudiencesJson = clientDef.OboAllowedSourceAudiences != null ? JsonSerializer.Serialize(clientDef.OboAllowedSourceAudiences) : null,
                OboAllowedTargetAudiencesJson = clientDef.OboAllowedTargetAudiences != null ? JsonSerializer.Serialize(clientDef.OboAllowedTargetAudiences) : null,
                OboAllowedScopesJson = clientDef.OboAllowedScopes != null ? JsonSerializer.Serialize(clientDef.OboAllowedScopes) : null,
                OboMaxDelegationDepth = clientDef.OboMaxDelegationDepth,
                OboMaxLifetimeMinutes = clientDef.OboMaxLifetimeMinutes,
                OboDpopMode = Enum.TryParse<OboDpopMode>(clientDef.OboDpopMode, out var dpopMode) ? dpopMode : null,
                OboAllowedCallersJson = clientDef.OboAllowedCallers != null ? JsonSerializer.Serialize(clientDef.OboAllowedCallers) : null,
                M2MAllowedAudiencesJson = clientDef.M2mAllowedAudiences != null ? JsonSerializer.Serialize(clientDef.M2mAllowedAudiences) : null,
                M2MAccessTokenLifetimeSeconds = clientDef.M2mAccessTokenLifetimeSeconds,
                PublicJwksJson = clientDef.PublicJwksJson,
                PublicJwksUri = clientDef.PublicJwksUri,
                AutoAssignNewUsersToClient = clientDef.AutoAssignNewUsersToClient ?? false
            };
            _dbContext.Clients.Add(client);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Add client secret if provided and not obfuscated
            if (!string.IsNullOrEmpty(clientDef.ClientSecretHash) && !ExportManifest.IsObfuscated(clientDef.ClientSecretHash))
            {
                // If we have a replacement secret in options, use that instead
                var secretHash = options.Secrets.TryGetValue(clientDef.ClientId, out var replacement)
                    ? replacement
                    : clientDef.ClientSecretHash;

                var clientSecret = new ClientSecret
                {
                    Id = GuidHelper.NewId(),
                    ClientId = client.Id,
                    SecretHash = secretHash,
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(90)
                };
                _dbContext.ClientSecrets.Add(clientSecret);
            }

            // Add client scopes
            foreach (var scopeName in clientDef.AllowedScopes ?? [])
            {
                var clientScope = new ClientScope
                {
                    ClientId = client.Id,
                    ScopeName = scopeName
                };
                _dbContext.ClientScopes.Add(clientScope);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportIdentityProvidersAsync(
        Guid tenantId,
        List<IdentityProviderSeedDefinition> providers,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var providerDef in providers)
        {
            var provider = new IdentityProvider
            {
                Id = GuidHelper.NewId(),
                TenantId = tenantId,
                Name = providerDef.Name,
                DisplayName = providerDef.DisplayName,
                Type = Enum.TryParse<IdentityProviderType>(providerDef.Type, true, out var type) ? type : IdentityProviderType.Oidc,
                Enabled = providerDef.Enabled ?? true,
                IsDefault = providerDef.IsDefault ?? false,
                AllowRegistration = providerDef.AllowRegistration ?? false,
                LogoUrl = providerDef.LogoUrl,
                SortOrder = providerDef.SortOrder ?? 0,
                ConfigJson = providerDef.Config != null ? JsonSerializer.Serialize(providerDef.Config) : null
            };
            _dbContext.IdentityProviders.Add(provider);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Add claim mappings
            foreach (var mappingDef in providerDef.ClaimMappings ?? [])
            {
                var mapping = new IdentityProviderClaimMapping
                {
                    Id = GuidHelper.NewId(),
                    IdentityProviderId = provider.Id,
                    ExternalClaim = mappingDef.ExternalClaim,
                    LocalClaim = mappingDef.LocalClaim,
                    Transform = mappingDef.Transform,
                    Order = mappingDef.Order ?? 0
                };
                _dbContext.IdentityProviderClaimMappings.Add(mapping);
            }

            // Add keys (public signing keys only)
            foreach (var keyDef in providerDef.Keys ?? [])
            {
                var key = new IdentityProviderKey
                {
                    Id = GuidHelper.NewId(),
                    IdentityProviderId = provider.Id,
                    Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(keyDef.Purpose, true, out var purpose) ? purpose : IdentityProviderKeyPurpose.Signing,
                    Alg = keyDef.Alg,
                    Kid = keyDef.Kid,
                    Jwk = keyDef.Jwk ?? string.Empty,
                    Active = keyDef.Active ?? true
                };
                _dbContext.IdentityProviderKeys.Add(key);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GenerateUniqueTenantSlugAsync(string baseSlug, CancellationToken cancellationToken)
    {
        var suffix = 1;
        var candidate = $"{baseSlug}_imported";
        
        while (await _dbContext.Tenants.AnyAsync(t => t.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseSlug}_imported_{suffix}";
        }
        
        return candidate;
    }

    private async Task<string> GenerateUniqueClientIdAsync(string baseClientId, CancellationToken cancellationToken)
    {
        var suffix = 1;
        var candidate = $"{baseClientId}_imported";
        
        while (await _dbContext.Clients.AnyAsync(c => c.ClientId == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseClientId}_imported_{suffix}";
        }
        
        return candidate;
    }

    private static string GenerateChecksum(SeedManifest data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static List<string> NormalizeScopeNames(IEnumerable<string>? scopes)
        => (scopes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task EnsureScopesExistAsync(Guid tenantId, List<string> scopeNames, CancellationToken cancellationToken)
    {
        if (scopeNames.Count == 0)
        {
            return;
        }

        var existing = await _dbContext.Scopes.AsNoTracking()
            .Where(s => scopeNames.Contains(s.Name))
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);

        var missing = scopeNames
            .Where(s => !existing.Contains(s, StringComparer.Ordinal))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var toSeed = missing.Where(s => AutoSeedableGlobalScopes.Contains(s)).ToList();
        if (toSeed.Count > 0)
        {
            foreach (var name in toSeed)
            {
                _dbContext.Scopes.Add(new Scope
                {
                    Name = name,
                    TenantId = null,
                    IsGlobal = true,
                    IsExposed = true
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            existing = await _dbContext.Scopes.AsNoTracking()
                .Where(s => scopeNames.Contains(s.Name))
                .Select(s => s.Name)
                .ToListAsync(cancellationToken);

            missing = scopeNames
                .Where(s => !existing.Contains(s, StringComparer.Ordinal))
                .ToList();
        }

        // Auto-create remaining missing scopes as tenant-scoped/custom scopes.
        // This keeps client import self-contained for new environments.
        if (missing.Count > 0)
        {
            foreach (var name in missing)
            {
                _dbContext.Scopes.Add(new Scope
                {
                    Name = name,
                    TenantId = tenantId,
                    IsGlobal = false,
                    IsExposed = true
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ImportValidationException(ImportError error) : Exception(error.Message)
    {
        public ImportError Error { get; } = error;
    }

    private async Task<Guid> LogImportAuditAsync(
        Guid? tenantId,
        string entityType,
        string entityIdentifier,
        ImportOptions options,
        bool success,
        int created,
        int updated,
        int skipped,
        string? errorDetails,
        string? checksum,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();

        var auditLog = new ConfigurationAuditLog
        {
            TenantId = tenantId,
            Operation = "Import",
            EntityType = entityType,
            EntityIdentifier = entityIdentifier,
            Result = success ? "Success" : "Failed",
            EntitiesCreated = created,
            EntitiesUpdated = updated,
            EntitiesSkipped = skipped,
            ErrorDetails = errorDetails,
            ManifestChecksum = checksum,
            PerformedBy = options.ImportedBy ?? "system",
            IpAddress = options.IpAddress,
            UserAgent = options.UserAgent
        };

        _dbContext.ConfigurationAuditLogs.Add(auditLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return auditLog.Id;
    }
}
