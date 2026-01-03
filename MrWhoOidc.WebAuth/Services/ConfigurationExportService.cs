using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for exporting OIDC configuration to portable JSON format.
/// </summary>
public sealed class ConfigurationExportService(
    AuthDbContext dbContext,
    ILogger<ConfigurationExportService> logger) : IConfigurationExportService
{
    private readonly AuthDbContext _dbContext = dbContext;
    private readonly ILogger<ConfigurationExportService> _logger = logger;

    private static List<ScopeSeedDefinition> BuildScopeSeedDefinitions(
        List<string> referencedScopeNames,
        List<Scope> scopes,
        Tenant tenant)
    {
        if (referencedScopeNames.Count == 0)
        {
            return [];
        }

        var scopesByName = scopes.ToDictionary(s => s.Name, StringComparer.Ordinal);

        var result = new List<ScopeSeedDefinition>(referencedScopeNames.Count);
        foreach (var scopeName in referencedScopeNames.Distinct(StringComparer.Ordinal))
        {
            if (scopesByName.TryGetValue(scopeName, out var scope))
            {
                result.Add(new ScopeSeedDefinition
                {
                    Name = scope.Name,
                    Description = scope.Description,
                    IsGlobal = scope.IsGlobal,
                    IsExposed = scope.IsExposed,
                    TenantSlug = scope.TenantId == tenant.Id ? tenant.Slug : null
                });
            }
            else
            {
                // Scope referenced by client but missing from catalog.
                // Export a minimal definition so import can recreate it if desired.
                result.Add(new ScopeSeedDefinition
                {
                    Name = scopeName,
                    IsGlobal = false,
                    IsExposed = true,
                    TenantSlug = tenant.Slug
                });
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ExportManifest> ExportTenantAsync(
        Guid tenantId,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting tenant {TenantId} with mode {Mode}", tenantId, options.Mode);

        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

        // Load all realms for this tenant
        var realms = await _dbContext.Realms
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Load all clients for this tenant
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Load client secrets
        var clientIds = clients.Select(c => c.Id).ToList();
        var clientSecrets = await _dbContext.ClientSecrets
            .AsNoTracking()
            .Where(cs => clientIds.Contains(cs.ClientId))
            .ToListAsync(cancellationToken);

        // Load client scopes
        var clientScopes = await _dbContext.ClientScopes
            .AsNoTracking()
            .Where(cs => clientIds.Contains(cs.ClientId))
            .ToListAsync(cancellationToken);

        // Load scopes for the tenant
        var scopes = await _dbContext.Scopes
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId || s.TenantId == null)
            .ToListAsync(cancellationToken);

        // Load identity providers for this tenant
        var providers = await _dbContext.IdentityProviders
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var providerIds = providers.Select(p => p.Id).ToList();
        var claimMappings = await _dbContext.IdentityProviderClaimMappings
            .AsNoTracking()
            .Where(m => providerIds.Contains(m.IdentityProviderId))
            .ToListAsync(cancellationToken);

        var providerKeys = await _dbContext.IdentityProviderKeys
            .AsNoTracking()
            .Where(k => providerIds.Contains(k.IdentityProviderId))
            .ToListAsync(cancellationToken);

        // Load client-IdP assignments
        var clientIdpAssignments = await _dbContext.ClientIdentityProviders
            .AsNoTracking()
            .Where(cip => clientIds.Contains(cip.ClientId))
            .ToListAsync(cancellationToken);

        // Load roles for this tenant
        var realmIds = realms.Select(r => r.Id).ToList();
        var roles = await _dbContext.Roles
            .AsNoTracking()
            .Where(r => realmIds.Contains(r.RealmId))
            .ToListAsync(cancellationToken);

        // Build the seed manifest
        var seedManifest = BuildSeedManifest(
            tenant, realms, clients, clientSecrets, clientScopes, scopes,
            providers, claimMappings, providerKeys, clientIdpAssignments, roles,
            options.Mode);

        var exportManifest = new ExportManifest
        {
            ExportType = "tenant",
            ExportMode = options.Mode == ExportMode.Obfuscated ? "obfuscated" : "full",
            Metadata = BuildMetadata(options, tenant.Slug),
            Data = seedManifest
        };

        // Generate checksum if requested
        if (options.IncludeChecksum)
        {
            exportManifest = exportManifest with
            {
                Metadata = exportManifest.Metadata with
                {
                    Checksum = GenerateChecksum(exportManifest.Data)
                }
            };
        }

        // Log audit
        await LogExportAuditAsync(tenant.Id, "Tenant", tenant.Slug, options, true, cancellationToken);

        _logger.LogInformation("Exported tenant {TenantSlug} with {RealmCount} realms, {ClientCount} clients, {ProviderCount} providers",
            tenant.Slug, realms.Count, clients.Count, providers.Count);

        return exportManifest;
    }

    /// <inheritdoc />
    public async Task<ExportManifest> ExportRealmAsync(
        Guid realmId,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting realm {RealmId} with mode {Mode}", realmId, options.Mode);

        var realm = await _dbContext.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == realmId, cancellationToken)
            ?? throw new InvalidOperationException($"Realm {realmId} not found");

        // Load tenant separately (no navigation property)
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == realm.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {realm.TenantId} not found");

        // Load all clients for this realm
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => c.RealmId == realmId)
            .ToListAsync(cancellationToken);

        var clientIds = clients.Select(c => c.Id).ToList();
        var clientSecrets = await _dbContext.ClientSecrets
            .AsNoTracking()
            .Where(cs => clientIds.Contains(cs.ClientId))
            .ToListAsync(cancellationToken);

        var clientScopes = await _dbContext.ClientScopes
            .AsNoTracking()
            .Where(cs => clientIds.Contains(cs.ClientId))
            .ToListAsync(cancellationToken);

        // Load identity providers for the tenant (available to this realm)
        var providers = await _dbContext.IdentityProviders
            .AsNoTracking()
            .Where(p => p.TenantId == realm.TenantId)
            .ToListAsync(cancellationToken);

        var providerLookup = providers.ToDictionary(p => p.Id, p => p.Name);

        var clientIdpAssignments = await _dbContext.ClientIdentityProviders
            .AsNoTracking()
            .Where(cip => clientIds.Contains(cip.ClientId))
            .ToListAsync(cancellationToken);

        // Load roles for this realm
        var roles = await _dbContext.Roles
            .AsNoTracking()
            .Where(r => r.RealmId == realmId)
            .ToListAsync(cancellationToken);

        var secretsByClient = clientSecrets.GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.First());
        var scopesByClient = clientScopes.GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.ToList());
        var idpAssignmentsByClient = clientIdpAssignments.GroupBy(a => a.ClientId).ToDictionary(g => g.Key, g => g.ToList());
        var realmLookup = new Dictionary<Guid, string> { [realm.Id] = realm.Name };

        var realmDefinition = new RealmSeedDefinition
        {
            Name = realm.Name,
            DisplayName = realm.DisplayName,
            AllowUnconfirmedLogin = realm.AllowUnconfirmedLogin,
            Clients = clients.Select(c => BuildClientSeedDefinition(
                c, secretsByClient, scopesByClient, idpAssignmentsByClient,
                realmLookup, providerLookup, options.Mode)).ToList(),
            Roles = roles.Select(r => new RoleSeedDefinition
            {
                Name = r.Name,
                RealmName = realm.Name,
                IsActive = r.IsActive
            }).ToList()
        };

        var referencedScopeNames = realmDefinition.Clients
            .SelectMany(c => c.AllowedScopes)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var referencedScopes = referencedScopeNames.Count == 0
            ? []
            : await _dbContext.Scopes
                .AsNoTracking()
                .Where(s => referencedScopeNames.Contains(s.Name) && (s.TenantId == null || s.TenantId == tenant.Id))
                .ToListAsync(cancellationToken);

        var exportManifest = new ExportManifest
        {
            ExportType = "realm",
            ExportMode = options.Mode == ExportMode.Obfuscated ? "obfuscated" : "full",
            Metadata = BuildMetadata(options, $"{tenant.Slug}/{realm.Name}"),
            Data = new SeedManifest
            {
                Version = 1,
                Scopes = BuildScopeSeedDefinitions(referencedScopeNames, referencedScopes, tenant),
                Realms = [realmDefinition]
            }
        };

        if (options.IncludeChecksum)
        {
            exportManifest = exportManifest with
            {
                Metadata = exportManifest.Metadata with { Checksum = GenerateChecksum(exportManifest.Data) }
            };
        }

        await LogExportAuditAsync(realm.TenantId, "Realm", realm.Name, options, true, cancellationToken);

        _logger.LogInformation("Exported realm {RealmName} with {ClientCount} clients, {RoleCount} roles",
            realm.Name, clients.Count, roles.Count);

        return exportManifest;
    }

    /// <inheritdoc />
    public async Task<ExportManifest> ExportClientAsync(
        Guid clientId,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting client {ClientId} with mode {Mode}", clientId, options.Mode);

        var client = await _dbContext.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken)
            ?? throw new InvalidOperationException($"Client {clientId} not found");

        // Load realm separately (no navigation property)
        var realm = await _dbContext.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == client.RealmId, cancellationToken)
            ?? throw new InvalidOperationException($"Realm {client.RealmId} not found");

        // Load tenant separately (no navigation property)
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == client.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {client.TenantId} not found");

        var secrets = await _dbContext.ClientSecrets
            .AsNoTracking()
            .Where(cs => cs.ClientId == clientId)
            .ToListAsync(cancellationToken);

        var scopes = await _dbContext.ClientScopes
            .AsNoTracking()
            .Where(cs => cs.ClientId == clientId)
            .ToListAsync(cancellationToken);

        var idpAssignments = await _dbContext.ClientIdentityProviders
            .AsNoTracking()
            .Where(cip => cip.ClientId == clientId)
            .ToListAsync(cancellationToken);

        // Load provider names for assignments
        var providerIds = idpAssignments.Select(a => a.IdentityProviderId).Distinct().ToList();
        var providers = await _dbContext.IdentityProviders
            .AsNoTracking()
            .Where(p => providerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var secretsByClient = secrets.Count > 0
            ? new Dictionary<Guid, ClientSecret> { [clientId] = secrets.First() }
            : new Dictionary<Guid, ClientSecret>();
        var scopesByClient = new Dictionary<Guid, List<ClientScope>> { [clientId] = scopes };
        var idpAssignmentsByClient = new Dictionary<Guid, List<ClientIdentityProvider>> { [clientId] = idpAssignments };
        var realmLookup = new Dictionary<Guid, string> { [realm.Id] = realm.Name };

        var clientDefinition = BuildClientSeedDefinition(
            client, secretsByClient, scopesByClient, idpAssignmentsByClient,
            realmLookup, providers, options.Mode);

        var referencedScopeNames = clientDefinition.AllowedScopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var referencedScopes = referencedScopeNames.Count == 0
            ? []
            : await _dbContext.Scopes
                .AsNoTracking()
                .Where(s => referencedScopeNames.Contains(s.Name) && (s.TenantId == null || s.TenantId == tenant.Id))
                .ToListAsync(cancellationToken);

        var exportManifest = new ExportManifest
        {
            ExportType = "client",
            ExportMode = options.Mode == ExportMode.Obfuscated ? "obfuscated" : "full",
            Metadata = BuildMetadata(options, $"{tenant.Slug}/{realm.Name}/{client.ClientId}"),
            Data = new SeedManifest
            {
                Version = 1,
                Scopes = BuildScopeSeedDefinitions(referencedScopeNames, referencedScopes, tenant),
                Clients = [clientDefinition]
            }
        };

        if (options.IncludeChecksum)
        {
            exportManifest = exportManifest with
            {
                Metadata = exportManifest.Metadata with { Checksum = GenerateChecksum(exportManifest.Data) }
            };
        }

        await LogExportAuditAsync(client.TenantId, "Client", client.ClientId, options, true, cancellationToken);

        _logger.LogInformation("Exported client {ClientId} from realm {RealmName}", client.ClientId, realm.Name);

        return exportManifest;
    }

    /// <inheritdoc />
    public async Task<ExportManifest> ExportIdentityProviderAsync(
        Guid providerId,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting identity provider {ProviderId} with mode {Mode}", providerId, options.Mode);

        var provider = await _dbContext.IdentityProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Identity provider {providerId} not found");

        // Load tenant separately (no navigation property)
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == provider.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {provider.TenantId} not found");

        var claimMappings = await _dbContext.IdentityProviderClaimMappings
            .AsNoTracking()
            .Where(m => m.IdentityProviderId == providerId)
            .ToListAsync(cancellationToken);

        var providerKeys = await _dbContext.IdentityProviderKeys
            .AsNoTracking()
            .Where(k => k.IdentityProviderId == providerId)
            .ToListAsync(cancellationToken);

        var mappingsByProvider = new Dictionary<Guid, List<IdentityProviderClaimMapping>> { [providerId] = claimMappings };
        var keysByProvider = new Dictionary<Guid, List<IdentityProviderKey>> { [providerId] = providerKeys };

        var providerDefinition = BuildProviderSeedDefinition(provider, mappingsByProvider, keysByProvider, options.Mode);

        var exportManifest = new ExportManifest
        {
            ExportType = "provider",
            ExportMode = options.Mode == ExportMode.Obfuscated ? "obfuscated" : "full",
            Metadata = BuildMetadata(options, $"{tenant.Slug}/{provider.Name}"),
            Data = new SeedManifest
            {
                Version = 1,
                IdentityProviders = [providerDefinition]
            }
        };

        if (options.IncludeChecksum)
        {
            exportManifest = exportManifest with
            {
                Metadata = exportManifest.Metadata with { Checksum = GenerateChecksum(exportManifest.Data) }
            };
        }

        await LogExportAuditAsync(provider.TenantId, "IdentityProvider", provider.Name, options, true, cancellationToken);

        _logger.LogInformation("Exported identity provider {ProviderName} with {MappingCount} claim mappings",
            provider.Name, claimMappings.Count);

        return exportManifest;
    }

    private SeedManifest BuildSeedManifest(
        Tenant tenant,
        List<Realm> realms,
        List<Client> clients,
        List<ClientSecret> clientSecrets,
        List<ClientScope> clientScopes,
        List<Scope> scopes,
        List<IdentityProvider> providers,
        List<IdentityProviderClaimMapping> claimMappings,
        List<IdentityProviderKey> providerKeys,
        List<ClientIdentityProvider> clientIdpAssignments,
        List<Role> roles,
        ExportMode mode)
    {
        var realmLookup = realms.ToDictionary(r => r.Id, r => r.Name);
        var providerLookup = providers.ToDictionary(p => p.Id, p => p.Name);
        var secretsByClient = clientSecrets.GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.First());
        var scopesByClient = clientScopes.GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.ToList());
        var idpAssignmentsByClient = clientIdpAssignments.GroupBy(a => a.ClientId).ToDictionary(g => g.Key, g => g.ToList());
        var mappingsByProvider = claimMappings.GroupBy(m => m.IdentityProviderId).ToDictionary(g => g.Key, g => g.ToList());
        var keysByProvider = providerKeys.GroupBy(k => k.IdentityProviderId).ToDictionary(g => g.Key, g => g.ToList());

        return new SeedManifest
        {
            Version = 1,
            Scopes = scopes.Select(s => new ScopeSeedDefinition
            {
                Name = s.Name,
                Description = s.Description,
                IsGlobal = s.IsGlobal,
                IsExposed = s.IsExposed,
                TenantSlug = s.TenantId == tenant.Id ? tenant.Slug : null
            }).ToList(),
            Tenants =
            [
                new TenantSeedDefinition
                {
                    Slug = tenant.Slug,
                    Name = tenant.Name,
                    Description = tenant.Description,
                    IssuerUri = tenant.IssuerUri,
                    AdminEmail = tenant.AdminEmail,
                    BillingPlan = tenant.BillingPlan,
                    Status = tenant.Status.ToString(),
                    LogoUrl = tenant.LogoUrl,
                    PrimaryColor = tenant.PrimaryColor,
                    AccentColor = tenant.AccentColor,
                    SettingsJson = tenant.SettingsJson,
                    MaxUsers = tenant.MaxUsers,
                    MaxClients = tenant.MaxClients,
                    Realms = realms.Select(r => new RealmSeedDefinition
                    {
                        Name = r.Name,
                        DisplayName = r.DisplayName,
                        AllowUnconfirmedLogin = r.AllowUnconfirmedLogin
                    }).ToList(),
                    Clients = clients.Select(c => BuildClientSeedDefinition(
                        c, secretsByClient, scopesByClient, idpAssignmentsByClient,
                        realmLookup, providerLookup, mode)).ToList(),
                    IdentityProviders = providers.Select(p => BuildProviderSeedDefinition(
                        p, mappingsByProvider, keysByProvider, mode)).ToList(),
                    Roles = roles.Select(r => new RoleSeedDefinition
                    {
                        Name = r.Name,
                        RealmName = realmLookup.GetValueOrDefault(r.RealmId, "admin"),
                        IsActive = r.IsActive
                    }).ToList()
                }
            ]
        };
    }

    private static ClientSeedDefinition BuildClientSeedDefinition(
        Client client,
        Dictionary<Guid, ClientSecret> secretsByClient,
        Dictionary<Guid, List<ClientScope>> scopesByClient,
        Dictionary<Guid, List<ClientIdentityProvider>> idpAssignmentsByClient,
        Dictionary<Guid, string> realmLookup,
        Dictionary<Guid, string> providerLookup,
        ExportMode mode)
    {
        var scopeNames = scopesByClient.TryGetValue(client.Id, out var clientScopes)
            ? clientScopes.Select(cs => cs.ScopeName).ToList()
            : [];

        var idpAssignments = idpAssignmentsByClient.TryGetValue(client.Id, out var assignments)
            ? assignments.Select(a => new ClientIdpAssignmentSeedDefinition
            {
                ProviderName = providerLookup.GetValueOrDefault(a.IdentityProviderId, a.IdentityProviderId.ToString()),
                Enabled = a.Enabled,
                IsDefaultForClient = a.IsDefaultForClient,
                AutoRedirectIfSingle = a.AutoRedirectIfSingle,
                RequiredAcr = a.RequiredAcr,
                Order = a.Order
            }).ToList()
            : [];

        string? secretValue = null;
        if (secretsByClient.TryGetValue(client.Id, out var secret))
        {
            secretValue = mode == ExportMode.Full ? secret.SecretHash : ExportManifest.ObfuscateSecret(secret.SecretHash);
        }

        return new ClientSeedDefinition
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName ?? client.ClientId,
            Realm = realmLookup.GetValueOrDefault(client.RealmId, "admin"),
            RequirePkce = client.RequirePkce,
            RequireConsent = client.RequireConsent,
            RequirePar = client.RequirePar,
            AutoApprovalMode = client.AutoApprovalMode.ToString(),
            ClientSecretHash = secretValue,
            PublicJwksJson = client.PublicJwksJson,
            PublicJwksUri = client.PublicJwksUri,
            AllowedLoginRedirectUris = ParseJsonArray(client.AllowedLoginRedirectUrisJson) ?? [],
            AllowedLogoutRedirectUris = ParseJsonArray(client.AllowedLogoutRedirectUrisJson) ?? [],
            SubjectType = client.SubjectType,
            SectorIdentifierUri = client.SectorIdentifierUri,
            AllowLocalLogin = client.AllowLocalLogin,
            AllowExternalIdp = client.AllowExternalIdp,
            AllowQrLogin = client.AllowQrLogin,
            BackChannelLogoutUri = client.BackChannelLogoutUri,
            BackChannelLogoutSessionRequired = client.BackChannelLogoutSessionRequired,
            FrontChannelLogoutUri = client.FrontChannelLogoutUri,
            FrontChannelLogoutSessionRequired = client.FrontChannelLogoutSessionRequired,
            AllowedScopes = scopeNames,
            OboEnabled = client.OboEnabled,
            OboAllowedSourceAudiences = ParseJsonArray(client.OboAllowedSourceAudiencesJson) ?? [],
            OboAllowedTargetAudiences = ParseJsonArray(client.OboAllowedTargetAudiencesJson) ?? [],
            OboAllowedScopes = ParseJsonArray(client.OboAllowedScopesJson) ?? [],
            OboMaxDelegationDepth = client.OboMaxDelegationDepth,
            OboMaxLifetimeMinutes = client.OboMaxLifetimeMinutes,
            OboDpopMode = client.OboDpopMode?.ToString(),
            OboAllowedCallers = ParseJsonArray(client.OboAllowedCallersJson) ?? [],
            M2mAllowedAudiences = ParseJsonArray(client.M2MAllowedAudiencesJson) ?? [],
            M2mAccessTokenLifetimeSeconds = client.M2MAccessTokenLifetimeSeconds,
            AutoAssignNewUsersToClient = client.AutoAssignNewUsersToClient,
            IdentityProviderAssignments = idpAssignments
        };
    }

    private static List<string>? ParseJsonArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }

    private static IdentityProviderSeedDefinition BuildProviderSeedDefinition(
        IdentityProvider provider,
        Dictionary<Guid, List<IdentityProviderClaimMapping>> mappingsByProvider,
        Dictionary<Guid, List<IdentityProviderKey>> keysByProvider,
        ExportMode mode)
    {
        var config = string.IsNullOrEmpty(provider.ConfigJson)
            ? null
            : ObfuscateProviderConfig(provider.ConfigJson, mode);

        var mappings = mappingsByProvider.TryGetValue(provider.Id, out var providerMappings)
            ? providerMappings.Select(m => new ClaimMappingSeedDefinition
            {
                ExternalClaim = m.ExternalClaim,
                LocalClaim = m.LocalClaim,
                Transform = m.Transform,
                Order = m.Order
            }).ToList()
            : [];

        var keys = keysByProvider.TryGetValue(provider.Id, out var providerKeys)
            ? providerKeys
                .Where(k => k.Purpose == IdentityProviderKeyPurpose.Signing) // Only export public signing keys
                .Select(k => new ProviderKeySeedDefinition
                {
                    Purpose = k.Purpose.ToString().ToLowerInvariant(),
                    Alg = k.Alg,
                    Kid = k.Kid,
                    Jwk = k.Jwk, // Public key only
                    Active = k.Active
                }).ToList()
            : [];

        return new IdentityProviderSeedDefinition
        {
            Name = provider.Name,
            DisplayName = provider.DisplayName,
            Type = provider.Type.ToString().ToLowerInvariant(),
            Enabled = provider.Enabled,
            IsDefault = provider.IsDefault,
            AllowRegistration = provider.AllowRegistration,
            LogoUrl = provider.LogoUrl,
            SortOrder = provider.SortOrder,
            Config = config,
            ClaimMappings = mappings,
            Keys = keys
        };
    }

    private static Dictionary<string, object?>? ObfuscateProviderConfig(string configJson, ExportMode mode)
    {
        try
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, object?>>(configJson);
            if (config == null) return null;

            if (mode == ExportMode.Obfuscated)
            {
                // Obfuscate sensitive fields
                var sensitiveKeys = new[] { "clientSecret", "ClientSecret", "client_secret" };
                foreach (var key in sensitiveKeys)
                {
                    if (config.ContainsKey(key) && config[key] != null)
                    {
                        config[key] = ExportManifest.ObfuscatedMarker;
                    }
                }
            }

            return config;
        }
        catch
        {
            return null;
        }
    }

    private static ExportMetadata BuildMetadata(ExportOptions options, string? sourceTenant)
    {
        return new ExportMetadata
        {
            ExportedAt = DateTimeOffset.UtcNow,
            ExportedBy = options.ExportedBy,
            SourceSystem = options.SourceSystem ?? Environment.MachineName,
            SourceVersion = typeof(ConfigurationExportService).Assembly.GetName().Version?.ToString(),
            SourceTenant = sourceTenant
        };
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

    private async Task LogExportAuditAsync(
        Guid? tenantId,
        string entityType,
        string entityIdentifier,
        ExportOptions options,
        bool success,
        CancellationToken cancellationToken)
    {
        var auditLog = new ConfigurationAuditLog
        {
            TenantId = tenantId,
            Operation = "Export",
            EntityType = entityType,
            EntityIdentifier = entityIdentifier,
            ExportMode = options.Mode.ToString(),
            Result = success ? "Success" : "Failed",
            PerformedBy = options.ExportedBy ?? "unknown"
        };

        _dbContext.ConfigurationAuditLogs.Add(auditLog);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
