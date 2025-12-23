using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
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
        }

        // Detect conflicts
        var conflicts = await DetectConflictsAsync(manifest, options, cancellationToken);
        
        // Build entity lists
        var (toCreate, toUpdate) = BuildEntityLists(manifest, conflicts, options);

        preview = preview with
        {
            IsValid = true,
            Conflicts = conflicts,
            EntitiesToCreate = toCreate,
            EntitiesToUpdate = toUpdate,
            Warnings = warnings
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
    public Task<ImportResult> ImportRealmAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Will be implemented in Phase 5
        throw new NotImplementedException("Realm import will be implemented in Phase 5");
    }

    /// <inheritdoc />
    public Task<ImportResult> ImportClientAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Will be implemented in Phase 6
        throw new NotImplementedException("Client import will be implemented in Phase 6");
    }

    /// <inheritdoc />
    public Task<ImportResult> ImportIdentityProviderAsync(
        ExportManifest manifest,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        // Will be implemented in Phase 7
        throw new NotImplementedException("Identity provider import will be implemented in Phase 7");
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
                    Jwk = keyDef.Jwk,
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
