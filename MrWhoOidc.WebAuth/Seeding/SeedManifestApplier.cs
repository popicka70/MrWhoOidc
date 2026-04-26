using System.Text.Json;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Claims;
using MrWhoOidc.Auth.Licensing.Services;

namespace MrWhoOidc.WebAuth.Seeding;

public interface ISeedManifestApplier
{
    Task ApplyTenantsAsync(SeedManifest manifest, string authorityBaseUrl, CancellationToken ct = default);
    Task ApplyLicensesAsync(SeedManifest manifest, CancellationToken ct = default);
    Task ApplyForCurrentTenantAsync(SeedManifest manifest, CancellationToken ct = default);
}

internal sealed class SeedManifestApplier(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
#pragma warning disable CS9113 // Parameter is unread - kept for future multi-tenancy validation
    IMultiTenancyOptions multiTenancyOptions,
#pragma warning restore CS9113
    IIssuerBuilder issuerBuilder,
    IOptions<OidcOptions> oidcOptions,
    IOptions<SeedManifestOptions> seedOptions,
    IConfiguration configuration,
    IPasswordHasher passwordHasher,
    IClientStore clientStore,
    IPlatformSettingsService platformSettingsService,
    ILicenseService licenseService,
    IUserAccountProvisioner accountProvisioner,
    ILogger<SeedManifestApplier> logger) : ISeedManifestApplier
{
    private const string SeededAdminUsername = "admin";
    private const string LicensingAdminClientId = "licensing-admin";
    private const string LicensingAdminRoleName = "licensing-admin";

    public async Task ApplyLicensesAsync(SeedManifest manifest, CancellationToken ct = default)
    {
        if (manifest.Licenses.Count == 0)
        {
            return;
        }

        foreach (var licenseDef in manifest.Licenses)
        {
            try
            {
                var token = await TryResolveLicenseTokenAsync(licenseDef, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    logger.LogWarning("Seed manifest license definition had no token (licenseToken/licenseTokenPath/licenseTokenEnv).");
                    continue;
                }

                var scope = licenseDef.Scope?.Trim();
                var isTenantScope = string.Equals(scope, "tenant", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(scope) && !string.IsNullOrWhiteSpace(licenseDef.TenantSlug));

                Guid? tenantId = null;
                if (isTenantScope)
                {
                    if (string.IsNullOrWhiteSpace(licenseDef.TenantSlug))
                    {
                        logger.LogWarning("Seed manifest tenant-scoped license missing tenantSlug.");
                        continue;
                    }

                    var slug = licenseDef.TenantSlug.Trim();
                    var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct).ConfigureAwait(false);
                    if (tenant is null)
                    {
                        logger.LogWarning("Seed manifest license references unknown tenant '{TenantSlug}'.", slug);
                        continue;
                    }

                    tenantId = tenant.Id;
                }

                var alreadyInstalled = await db.Licenses.AsNoTracking().AnyAsync(l => l.TenantId == tenantId && l.IsActive, ct).ConfigureAwait(false);
                if (alreadyInstalled && !seedOptions.Value.OverwriteLicense)
                {
                    logger.LogInformation(
                        "Seed manifest skipped license install for {Scope} (license exists and Seeding__OverwriteLicense=false).",
                        tenantId.HasValue ? $"tenant '{licenseDef.TenantSlug}'" : "platform");
                    continue;
                }

                var result = await licenseService.InstallLicenseAsync(
                        token,
                        tenantId,
                        installedBy: null,
                        notes: string.IsNullOrWhiteSpace(licenseDef.Notes) ? "Installed via seed manifest" : licenseDef.Notes,
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (result.IsValid)
                {
                    logger.LogInformation(
                        "Seed manifest installed license for {Scope} (tier={Tier}).",
                        tenantId.HasValue ? $"tenant '{licenseDef.TenantSlug}'" : "platform",
                        result.LicenseInfo?.Tier ?? "unknown");
                }
                else
                {
                    logger.LogWarning(
                        "Seed manifest failed to install license for {Scope}: {ErrorCode} - {ErrorMessage}",
                        tenantId.HasValue ? $"tenant '{licenseDef.TenantSlug}'" : "platform",
                        result.ErrorCode,
                        result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                // Never fail startup/requests due to non-critical dev/test seeding.
                logger.LogWarning(ex, "Seed manifest license installation failed.");
            }
        }
    }

    private async Task<string?> TryResolveLicenseTokenAsync(LicenseSeedDefinition licenseDef, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(licenseDef.LicenseTokenPath))
        {
            var path = licenseDef.LicenseTokenPath.Trim();
            if (File.Exists(path))
            {
                var fileToken = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fileToken))
                {
                    return fileToken.Trim();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(licenseDef.LicenseTokenEnv))
        {
            var key = licenseDef.LicenseTokenEnv.Trim();
            var envToken = configuration[key];
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                return envToken.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(licenseDef.LicenseToken) ? null : licenseDef.LicenseToken.Trim();
    }

    public async Task ApplyTenantsAsync(SeedManifest manifest, string authorityBaseUrl, CancellationToken ct = default)
    {
        await ApplyPlatformSettingsAsync(manifest).ConfigureAwait(false);
        await ApplyPlatformInitialAccessTokensAsync(manifest, ct).ConfigureAwait(false);

        if (manifest.Tenants.Count == 0)
        {
            return;
        }

        foreach (var t in manifest.Tenants)
        {
            if (string.IsNullOrWhiteSpace(t.Slug) || string.IsNullOrWhiteSpace(t.Name))
            {
                continue;
            }

            var slug = t.Slug.Trim();
            var existing = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == slug, ct).ConfigureAwait(false);
            if (existing is null)
            {
                var issuerUri = !string.IsNullOrWhiteSpace(t.IssuerUri)
                    ? t.IssuerUri.Trim().TrimEnd('/')
                    : issuerBuilder.BuildIssuer(authorityBaseUrl, slug).TrimEnd('/');

                var options = oidcOptions.Value;
                var baseUrlFromConfig =
                    (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? options.PublicBaseUrl.TrimEnd('/') : null)
                    ?? (!string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/') : null);

                // issuerBuilder expects a clean authority base URL.
                // If config includes a path, ignore it.
                var computedBaseUrl = authorityBaseUrl;
                if (!string.IsNullOrWhiteSpace(baseUrlFromConfig) && Uri.TryCreate(baseUrlFromConfig, UriKind.Absolute, out var uri))
                {
                    computedBaseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                }

                if (string.IsNullOrWhiteSpace(t.IssuerUri))
                {
                    issuerUri = issuerBuilder.BuildIssuer(computedBaseUrl, slug).TrimEnd('/');
                }

                var tenant = new Tenant
                {
                    Slug = slug,
                    Name = t.Name.Trim(),
                    Description = t.Description,
                    IssuerUri = issuerUri,
                    Status = TenantStatus.Active,
                    MaxUsers = 100000,
                    MaxClients = 1000,
                    AdminEmail = string.IsNullOrWhiteSpace(t.AdminEmail) ? null : t.AdminEmail.Trim(),
                    BillingPlan = string.IsNullOrWhiteSpace(t.BillingPlan) ? "Enterprise" : t.BillingPlan.Trim(),
                    CreatedAt = DateTimeOffset.UtcNow
                };

                db.Tenants.Add(tenant);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                logger.LogInformation("Seed manifest created tenant {TenantSlug} (TenantId={TenantId})", tenant.Slug, tenant.Id);
            }
            else if (seedOptions.Value.AllowUpdates)
            {
                var changed = false;

                if (!string.IsNullOrWhiteSpace(t.Name) && existing.Name != t.Name)
                {
                    existing.Name = t.Name.Trim();
                    changed = true;
                }

                if (t.Description is not null && existing.Description != t.Description)
                {
                    existing.Description = t.Description;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(t.AdminEmail) && existing.AdminEmail != t.AdminEmail)
                {
                    existing.AdminEmail = t.AdminEmail.Trim();
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(t.BillingPlan) && existing.BillingPlan != t.BillingPlan)
                {
                    existing.BillingPlan = t.BillingPlan.Trim();
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(t.IssuerUri) && existing.IssuerUri != t.IssuerUri.Trim().TrimEnd('/'))
                {
                    existing.IssuerUri = t.IssuerUri.Trim().TrimEnd('/');
                    changed = true;
                }

                if (changed)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    logger.LogInformation("Seed manifest updated tenant {TenantSlug}", existing.Slug);
                }
            }
        }
    }

    public async Task ApplyForCurrentTenantAsync(SeedManifest manifest, CancellationToken ct = default)
    {
        await ApplyPlatformSettingsAsync(manifest).ConfigureAwait(false);
        await ApplyPlatformInitialAccessTokensAsync(manifest, ct).ConfigureAwait(false);

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            return;
        }

        var tenantDef = manifest.Tenants.FirstOrDefault(t => string.Equals(t.Slug, tenant.Slug, StringComparison.OrdinalIgnoreCase));
        if (tenantDef is null)
        {
            return;
        }

        // Realms (name-based)
        foreach (var realmDef in tenantDef.Realms)
        {
            if (string.IsNullOrWhiteSpace(realmDef.Name)) continue;

            var name = realmDef.Name.Trim();
            var existing = await db.Realms.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Name == name, ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.Realms.Add(new Realm
                {
                    Name = name,
                    DisplayName = realmDef.DisplayName ?? name,
                    TenantId = tenant.TenantId,
                    AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin ?? true
                });
            }
            else if (seedOptions.Value.AllowUpdates)
            {
                if (!string.IsNullOrWhiteSpace(realmDef.DisplayName)) existing.DisplayName = realmDef.DisplayName;
                if (realmDef.AllowUnconfirmedLogin is not null) existing.AllowUnconfirmedLogin = realmDef.AllowUnconfirmedLogin.Value;
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Roles (realm-name based)
        foreach (var roleDef in tenantDef.Roles)
        {
            if (string.IsNullOrWhiteSpace(roleDef.Name))
            {
                continue;
            }

            var roleName = roleDef.Name.Trim();
            var realmName = string.IsNullOrWhiteSpace(roleDef.RealmName) ? "default" : roleDef.RealmName.Trim();

            var realm = await db.Realms.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Name == realmName, ct).ConfigureAwait(false);
            if (realm is null)
            {
                realm = new Realm
                {
                    Name = realmName,
                    DisplayName = realmName,
                    TenantId = tenant.TenantId,
                    AllowUnconfirmedLogin = true
                };
                db.Realms.Add(realm);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var role = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RealmId == realm.Id && r.Name == roleName, ct).ConfigureAwait(false);
            if (role is null)
            {
                db.Roles.Add(new Role
                {
                    Name = roleName,
                    RealmId = realm.Id,
                    TenantId = tenant.TenantId,
                    IsActive = roleDef.IsActive ?? true
                });
            }
            else if (seedOptions.Value.AllowUpdates && roleDef.IsActive is not null)
            {
                role.IsActive = roleDef.IsActive.Value;
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await ApplyDynamicClientRegistrationRealmAsync(tenantDef, tenant, ct).ConfigureAwait(false);

        await EnsureScopesAsync(manifest, tenantDef, tenant, ct).ConfigureAwait(false);

        // Clients (reference realms by name)
        foreach (var clientDef in tenantDef.Clients)
        {
            if (string.IsNullOrWhiteSpace(clientDef.ClientId) || string.IsNullOrWhiteSpace(clientDef.ClientName))
            {
                continue;
            }

            var clientId = clientDef.ClientId.Trim();
            var realmName = string.IsNullOrWhiteSpace(clientDef.Realm) ? "admin" : clientDef.Realm.Trim();

            var realm = await db.Realms.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Name == realmName, ct).ConfigureAwait(false);
            if (realm is null)
            {
                realm = new Realm
                {
                    Name = realmName,
                    DisplayName = realmName,
                    TenantId = tenant.TenantId,
                    AllowUnconfirmedLogin = true
                };
                db.Realms.Add(realm);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var client = await db.Clients.FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.ClientId == clientId, ct).ConfigureAwait(false);
            if (client is null)
            {
                var resolvedSecret = ResolveClientSecret(clientDef, configuration);
#pragma warning disable CS0618 // legacy secret hash kept for backward compatibility
                client = new Client
                {
                    ClientId = clientId,
                    ClientName = clientDef.ClientName.Trim(),
                    RequirePkce = clientDef.RequirePkce ?? true,
                    RequireConsent = clientDef.RequireConsent ?? false,
                    RealmId = realm.Id,
                    TenantId = tenant.TenantId,
                    AutoApprovalMode = ParseAutoApprovalMode(clientDef.AutoApprovalMode),
                    AutoAssignNewUsersToClient = clientDef.AutoAssignNewUsersToClient ?? false,
                    ClientSecretHash = string.IsNullOrWhiteSpace(resolvedSecret) ? null : passwordHasher.Hash(resolvedSecret)
                };
#pragma warning restore CS0618

                ApplyRedirectUris(client, clientDef);

                ApplyOboPolicy(client, clientDef, allowUpdates: true);

                db.Clients.Add(client);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resolvedSecret))
                {
                    await EnsureSeededClientSecretAsync(client, resolvedSecret, ct).ConfigureAwait(false);
                }

                await EnsureClientScopesAsync(client, clientDef, ct).ConfigureAwait(false);

                await EnsureSeededAdminHasAdminRoleForClientAsync(client, ct).ConfigureAwait(false);

                await clientStore.InvalidateClientCacheAsync(client.ClientId, tenant.TenantId, ct).ConfigureAwait(false);

                logger.LogInformation("Seed manifest created client {ClientId} (Tenant={TenantSlug})", client.ClientId, tenant.Slug);
                continue;
            }

            // Updates/backfills
            var allowUpdates = seedOptions.Value.AllowUpdates;

            if (allowUpdates)
            {
                client.ClientName = clientDef.ClientName.Trim();
                if (clientDef.RequirePkce is not null) client.RequirePkce = clientDef.RequirePkce.Value;
                if (clientDef.RequireConsent is not null) client.RequireConsent = clientDef.RequireConsent.Value;
                if (!string.IsNullOrWhiteSpace(clientDef.AutoApprovalMode)) client.AutoApprovalMode = ParseAutoApprovalMode(clientDef.AutoApprovalMode);
                if (clientDef.AutoAssignNewUsersToClient is not null) client.AutoAssignNewUsersToClient = clientDef.AutoAssignNewUsersToClient.Value;

                // Move realm if requested (by name)
                if (client.RealmId != realm.Id) client.RealmId = realm.Id;

                ApplyRedirectUris(client, clientDef);

                ApplyOboPolicy(client, clientDef, allowUpdates: true);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(clientDef.AutoApprovalMode) && client.AutoApprovalMode == AutoApprovalMode.No)
                {
                    client.AutoApprovalMode = ParseAutoApprovalMode(clientDef.AutoApprovalMode);
                }

                if ((clientDef.AllowedLoginRedirectUris.Count > 0 || clientDef.AllowedLogoutRedirectUris.Count > 0)
                    && (string.IsNullOrWhiteSpace(client.AllowedLoginRedirectUrisJson) || string.IsNullOrWhiteSpace(client.AllowedLogoutRedirectUrisJson)))
                {
                    ApplyRedirectUris(client, clientDef);
                }

                ApplyOboPolicy(client, clientDef, allowUpdates: false);
            }

#pragma warning disable CS0618 // legacy secret hash kept for backward compatibility
            var resolvedClientSecret = ResolveClientSecret(clientDef, configuration);
            if (!string.IsNullOrWhiteSpace(resolvedClientSecret))
            {
                if (seedOptions.Value.OverwriteClientSecrets || string.IsNullOrWhiteSpace(client.ClientSecretHash))
                {
                    client.ClientSecretHash = passwordHasher.Hash(resolvedClientSecret);
                }

                // IMPORTANT: if the client already has active secrets in the new ClientSecrets table,
                // those take precedence over the legacy ClientSecretHash during validation.
                // Ensure the seeded secret is present and active so token endpoint auth works.
                await EnsureSeededClientSecretAsync(client, resolvedClientSecret, ct).ConfigureAwait(false);
            }
#pragma warning restore CS0618

            await EnsureClientScopesAsync(client, clientDef, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await clientStore.InvalidateClientCacheAsync(client.ClientId, tenant.TenantId, ct).ConfigureAwait(false);

            await EnsureSeededAdminHasAdminRoleForClientAsync(client, ct).ConfigureAwait(false);
        }

        await EnsureSeededUsersAsync(tenantDef, tenant, ct).ConfigureAwait(false);
    }

    private async Task ApplyPlatformSettingsAsync(SeedManifest manifest)
    {
        var platformDef = manifest.PlatformSettings;
        if (platformDef is null)
        {
            return;
        }

        var settings = await platformSettingsService.GetSettingsAsync().ConfigureAwait(false);
        var changed = false;

        if (platformDef.QrLoginAtDiscoveryEnabled is not null && settings.QrLoginAtDiscoveryEnabled != platformDef.QrLoginAtDiscoveryEnabled.Value)
        {
            settings.QrLoginAtDiscoveryEnabled = platformDef.QrLoginAtDiscoveryEnabled.Value;
            changed = true;
        }

        if (platformDef.DynamicClientRegistrationEnabled is not null && settings.DynamicClientRegistrationEnabled != platformDef.DynamicClientRegistrationEnabled.Value)
        {
            settings.DynamicClientRegistrationEnabled = platformDef.DynamicClientRegistrationEnabled.Value;
            changed = true;
        }

        if (platformDef.EnableTokenExchange is not null && settings.EnableTokenExchange != platformDef.EnableTokenExchange.Value)
        {
            settings.EnableTokenExchange = platformDef.EnableTokenExchange.Value;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        await platformSettingsService.UpdateSettingsAsync(settings, "seed-manifest").ConfigureAwait(false);
        logger.LogInformation("Seed manifest updated platform settings.");
    }

    private async Task ApplyPlatformInitialAccessTokensAsync(SeedManifest manifest, CancellationToken ct)
    {
        if (manifest.PlatformInitialAccessTokens.Count == 0)
        {
            return;
        }

        var changed = false;

        foreach (var tokenDef in manifest.PlatformInitialAccessTokens)
        {
            if (string.IsNullOrWhiteSpace(tokenDef.Token))
            {
                continue;
            }

            var plaintextToken = tokenDef.Token.Trim();
            var tokenHash = HashPlatformInitialAccessToken(plaintextToken);
            var existing = await db.PlatformInitialAccessTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
                .ConfigureAwait(false);

            var normalizedDescription = string.IsNullOrWhiteSpace(tokenDef.Description) ? null : tokenDef.Description.Trim();
            if (existing is null)
            {
                db.PlatformInitialAccessTokens.Add(new PlatformInitialAccessToken
                {
                    TokenHash = tokenHash,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = string.IsNullOrWhiteSpace(tokenDef.CreatedBy) ? "seed-manifest" : tokenDef.CreatedBy.Trim(),
                    Description = normalizedDescription,
                    RevokedAt = null,
                    RevokedBy = null
                });

                changed = true;
                continue;
            }

            if (!seedOptions.Value.AllowUpdates)
            {
                continue;
            }

            if (existing.RevokedAt is not null)
            {
                existing.RevokedAt = null;
                existing.RevokedBy = null;
                changed = true;
            }

            if (existing.Description != normalizedDescription)
            {
                existing.Description = normalizedDescription;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Seed manifest ensured platform initial access tokens.");
    }

    private async Task ApplyDynamicClientRegistrationRealmAsync(TenantSeedDefinition tenantDef, TenantContext tenant, CancellationToken ct)
    {
        if (tenantDef.DynamicClientRegistrationRealm is null)
        {
            return;
        }

        var tenantEntity = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.TenantId, ct).ConfigureAwait(false);
        if (tenantEntity is null)
        {
            return;
        }

        var desiredRealmName = tenantDef.DynamicClientRegistrationRealm.Trim();
        Guid? desiredRealmId = null;

        if (!string.IsNullOrWhiteSpace(desiredRealmName))
        {
            desiredRealmId = await db.Realms
                .AsNoTracking()
                .Where(r => r.TenantId == tenant.TenantId && r.Name == desiredRealmName)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (desiredRealmId is null)
            {
                logger.LogWarning(
                    "Seed manifest requested dynamic client registration realm '{RealmName}' for tenant '{TenantSlug}', but no matching realm exists.",
                    desiredRealmName,
                    tenant.Slug);
                return;
            }
        }

        var settings = string.IsNullOrWhiteSpace(tenantEntity.SettingsJson)
            ? new TenantSettings()
            : JsonSerializer.Deserialize<TenantSettings>(tenantEntity.SettingsJson) ?? new TenantSettings();

        settings.Auth ??= new AuthTenantSettings();

        if (!seedOptions.Value.AllowUpdates && settings.Auth.DynamicClientRegistrationRealmId is not null)
        {
            return;
        }

        if (settings.Auth.DynamicClientRegistrationRealmId == desiredRealmId)
        {
            return;
        }

        settings.Auth.DynamicClientRegistrationRealmId = desiredRealmId;
        tenantEntity.SettingsJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Seed manifest configured dynamic client registration realm for tenant '{TenantSlug}' to '{RealmName}'.",
            tenant.Slug,
            string.IsNullOrWhiteSpace(desiredRealmName) ? "disabled" : desiredRealmName);
    }

    private async Task EnsureSeededClientSecretAsync(Client client, string resolvedClientSecret, CancellationToken ct)
    {
        // If overwrite is enabled, replace active secrets with the seeded secret.
        // Otherwise, only seed when the client has no secrets at all.
        var overwrite = seedOptions.Value.OverwriteClientSecrets;

        var existingSecrets = await db.ClientSecrets
            .Where(s => s.ClientId == client.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasAnySecrets = existingSecrets.Count > 0;
        if (hasAnySecrets && !overwrite)
        {
            return;
        }

        var seedBy = "seed";

        // Create + activate + make primary
        var newSecret = await clientStore.CreateSecretAsync(client.Id, resolvedClientSecret, description: "seed", createdBy: seedBy, expiresAtUtc: null, ct: ct)
            .ConfigureAwait(false);

        await clientStore.ActivateSecretAsync(newSecret.Id, seedBy, ct).ConfigureAwait(false);
        await clientStore.SetPrimarySecretAsync(newSecret.Id, seedBy, ct).ConfigureAwait(false);

        // Revoke any existing secrets if overwriting
        if (existingSecrets.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var s in existingSecrets)
            {
                if (s.Id == newSecret.Id) continue;
                if (s.RevokedAtUtc != null) continue;
                s.RevokedAtUtc = now;
                s.RevokedBy = seedBy;
                s.IsPrimary = false;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Seed manifest ensured client secret for {ClientId} (overwrite={Overwrite}, revoked={RevokedCount})",
            client.ClientId,
            overwrite,
            overwrite ? existingSecrets.Count : 0);
    }

    private static string HashPlatformInitialAccessToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private async Task EnsureScopesAsync(SeedManifest manifest, TenantSeedDefinition tenantDef, TenantContext tenant, CancellationToken ct)
    {
        // Scopes must exist before we can create ClientScopes due to FK constraints.
        // We seed:
        // 1) Any explicit scope definitions in the manifest that apply to this tenant (global or tenant-scoped)
        // 2) Any scopes referenced by tenant clients' allowedScopes (implicit global scopes)

        var allowUpdates = seedOptions.Value.AllowUpdates;

        var explicitDefs = manifest.Scopes
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new
            {
                Name = s.Name.Trim(),
                Def = s
            })
            .Where(x =>
                string.IsNullOrWhiteSpace(x.Name) == false &&
                (
                    string.IsNullOrWhiteSpace(x.Def.TenantSlug) ||
                    string.Equals(x.Def.TenantSlug.Trim(), tenant.Slug, StringComparison.OrdinalIgnoreCase)
                ))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Def, StringComparer.Ordinal);

        var referencedScopeNames = tenantDef.Clients
            .SelectMany(c => c.AllowedScopes)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var namesToEnsure = explicitDefs.Keys
            .Concat(referencedScopeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (namesToEnsure.Length == 0)
        {
            return;
        }

        var existing = await db.Scopes
            .AsNoTracking()
            .Where(s => namesToEnsure.Contains(s.Name))
            .ToDictionaryAsync(s => s.Name, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        foreach (var name in namesToEnsure)
        {
            explicitDefs.TryGetValue(name, out var def);

            var desiredIsGlobal = def?.IsGlobal
                ?? (string.IsNullOrWhiteSpace(def?.TenantSlug));

            var desiredTenantId = desiredIsGlobal
                ? (Guid?)null
                : tenant.TenantId;

            var desiredIsExposed = def?.IsExposed ?? true;
            var desiredDescription = def?.Description;

            if (!existing.TryGetValue(name, out var current))
            {
                db.Scopes.Add(new Scope
                {
                    Name = name,
                    IsGlobal = desiredIsGlobal,
                    TenantId = desiredTenantId,
                    IsExposed = desiredIsExposed,
                    Description = desiredDescription
                });

                continue;
            }

            if (!allowUpdates)
            {
                continue;
            }

            // Avoid silently re-homing scopes between global/tenant namespaces.
            // (Name is the PK, so this would be a destructive semantic change.)
            var namespaceCompatible =
                current.IsGlobal == desiredIsGlobal &&
                current.TenantId == desiredTenantId;

            if (!namespaceCompatible)
            {
                logger.LogWarning(
                    "Seed manifest scope '{ScopeName}' conflicts with existing namespace (IsGlobal={IsGlobal}, TenantId={TenantId}). Skipping namespace update.",
                    name,
                    current.IsGlobal,
                    current.TenantId);
                continue;
            }

            var tracked = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name, ct).ConfigureAwait(false);
            if (tracked is null)
            {
                continue;
            }

            var changed = false;
            if (tracked.IsExposed != desiredIsExposed)
            {
                tracked.IsExposed = desiredIsExposed;
                changed = true;
            }

            if (desiredDescription is not null && tracked.Description != desiredDescription)
            {
                tracked.Description = desiredDescription;
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureSeededAdminHasAdminRoleForClientAsync(Client client, CancellationToken ct)
    {
        // E2E/dev quality-of-life: the built-in seeded admin user has admin role assignments for the built-in
        // admin clients, but seed-manifest-created clients (e.g., licensing-web) also need an admin role
        // assignment for roles to show up in /userinfo (roles are client-contextual).
        //
        // We keep this narrowly scoped:
        // - only for the seeded "admin" user
        // - only for clients in the "admin" realm
        // - only if the "admin" role exists for that realm
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return;
        }

        var adminRealm = await db.Realms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == "admin", ct)
            .ConfigureAwait(false);

        if (adminRealm is null || client.RealmId != adminRealm.Id)
        {
            return;
        }

        var adminUser = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Username == SeededAdminUsername, ct)
            .ConfigureAwait(false);

        if (adminUser is null)
        {
            return;
        }

        var adminRole = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RealmId == adminRealm.Id && r.Name == "admin", ct)
            .ConfigureAwait(false);

        if (adminRole is null)
        {
            return;
        }

        var hasClientAssignment = await db.UserClientAssignments.AnyAsync(
            a => a.UserId == adminUser.Id && a.ClientId == client.Id && a.RealmId == adminRealm.Id && a.IsActive,
            ct).ConfigureAwait(false);

        if (!hasClientAssignment)
        {
            db.UserClientAssignments.Add(new UserClientAssignment
            {
                UserId = adminUser.Id,
                ClientId = client.Id,
                RealmId = adminRealm.Id,
                IsActive = true
            });
        }

        var hasRoleAssignment = await db.UserRealmRoleAssignments.AnyAsync(
            a => a.UserId == adminUser.Id && a.RoleId == adminRole.Id && a.RealmId == adminRealm.Id && a.IsActive,
            ct).ConfigureAwait(false);

        if (!hasRoleAssignment)
        {
            db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                RealmId = adminRealm.Id,
                IsActive = true
            });
        }

        if (string.Equals(client.ClientId, LicensingAdminClientId, StringComparison.Ordinal))
        {
            var licensingAdminRole = await db.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RealmId == adminRealm.Id && r.Name == LicensingAdminRoleName, ct)
                .ConfigureAwait(false);

            if (licensingAdminRole is not null)
            {
                var hasLicensingAdminRole = await db.UserRealmRoleAssignments.AnyAsync(
                    a => a.UserId == adminUser.Id && a.RoleId == licensingAdminRole.Id && a.RealmId == adminRealm.Id && a.IsActive,
                    ct).ConfigureAwait(false);

                if (!hasLicensingAdminRole)
                {
                    db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
                    {
                        UserId = adminUser.Id,
                        RoleId = licensingAdminRole.Id,
                        RealmId = adminRealm.Id,
                        IsActive = true
                    });
                }
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void ApplyRedirectUris(Client client, ClientSeedDefinition def)
    {
        if (def.AllowedLoginRedirectUris.Count > 0)
        {
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(def.AllowedLoginRedirectUris);
        }

        if (def.AllowedLogoutRedirectUris.Count > 0)
        {
            client.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(def.AllowedLogoutRedirectUris);
        }
    }

    private static void ApplyOboPolicy(Client client, ClientSeedDefinition def, bool allowUpdates)
    {
        if (def.OboEnabled is not null)
        {
            if (allowUpdates || client.OboEnabled is null)
            {
                client.OboEnabled = def.OboEnabled;
            }
        }

        if (def.OboAllowedSourceAudiences.Count > 0)
        {
            if (allowUpdates || string.IsNullOrWhiteSpace(client.OboAllowedSourceAudiencesJson))
            {
                client.OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(def.OboAllowedSourceAudiences);
            }
        }

        if (def.OboAllowedTargetAudiences.Count > 0)
        {
            if (allowUpdates || string.IsNullOrWhiteSpace(client.OboAllowedTargetAudiencesJson))
            {
                client.OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(def.OboAllowedTargetAudiences);
            }
        }

        if (def.OboAllowedScopes.Count > 0)
        {
            if (allowUpdates || string.IsNullOrWhiteSpace(client.OboAllowedScopesJson))
            {
                client.OboAllowedScopesJson = JsonSerializer.Serialize(def.OboAllowedScopes);
            }
        }
    }

    private async Task EnsureClientScopesAsync(Client client, ClientSeedDefinition def, CancellationToken ct)
    {
        if (def.AllowedScopes.Count == 0)
        {
            return;
        }

        var requested = def.AllowedScopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0)
        {
            return;
        }

        var existing = await db.ClientScopes
            .AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var scope in requested.Except(existing, StringComparer.Ordinal))
        {
            db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = scope });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureSeededUsersAsync(TenantSeedDefinition tenantDef, TenantContext tenant, CancellationToken ct)
    {
        if (tenantDef.Users.Count == 0)
        {
            return;
        }

        var defaultRealmId = await db.Realms
            .AsNoTracking()
            .Where(r => r.TenantId == tenant.TenantId && r.Name == "default")
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        foreach (var userDef in tenantDef.Users)
        {
            if (string.IsNullOrWhiteSpace(userDef.Username))
            {
                continue;
            }

            var username = userDef.Username.Trim();
            var email = string.IsNullOrWhiteSpace(userDef.Email) ? null : userDef.Email.Trim();
            var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
            var emailVerified = userDef.EmailVerified ?? true;

            var user = await db.Users.FirstOrDefaultAsync(
                    u => u.TenantId == tenant.TenantId
                        && (u.Username == username || (normalizedEmail != null && u.NormalizedEmail == normalizedEmail)),
                    ct)
                .ConfigureAwait(false);

            if (user is null)
            {
                user = new User
                {
                    Username = username,
                    Email = email,
                    NormalizedEmail = normalizedEmail,
                    EmailVerified = emailVerified,
                    EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null,
                    TenantId = tenant.TenantId,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                db.Users.Add(user);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                logger.LogInformation("Seed manifest created user {Username} (Tenant={TenantSlug})", username, tenant.Slug);
            }
            else if (seedOptions.Value.AllowUpdates)
            {
                var changed = false;

                if (email is not null && user.Email != email)
                {
                    user.Email = email;
                    user.NormalizedEmail = normalizedEmail;
                    changed = true;
                }

                if (user.EmailVerified != emailVerified)
                {
                    user.EmailVerified = emailVerified;
                    user.EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null;
                    changed = true;
                }

                if (changed)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            var isTenantAdmin = userDef.Roles.Any(r => string.Equals(r.Role?.Trim(), "tenant-admin", StringComparison.OrdinalIgnoreCase));
            await accountProvisioner.EnsureAsync(user, tenant.TenantId, defaultRealmId, isTenantAdmin, ct, autoSave: true).ConfigureAwait(false);

            var resolvedPassword = ResolveUserPassword(userDef);
            if (!string.IsNullOrWhiteSpace(resolvedPassword))
            {
                var account = await db.UserAccounts.FirstOrDefaultAsync(
                        a => a.Id == user.Id || a.Username == username || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail),
                        ct)
                    .ConfigureAwait(false);

                if (account is not null && (seedOptions.Value.AllowUpdates || string.IsNullOrWhiteSpace(account.PasswordHash)))
                {
                    account.PasswordHash = passwordHasher.Hash(resolvedPassword);
                    account.HashAlgorithm = "argon2id";
                    account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
                    account.FailedLoginAttempts = 0;
                    account.LastFailedLoginAt = null;
                    account.LockedOutUntil = null;
                }
            }

            foreach (var roleAssignment in userDef.Roles)
            {
                if (string.IsNullOrWhiteSpace(roleAssignment.Role))
                {
                    continue;
                }

                var realmName = string.IsNullOrWhiteSpace(roleAssignment.Realm) ? "default" : roleAssignment.Realm.Trim();
                var role = await db.Roles
                    .AsNoTracking()
                    .Join(
                        db.Realms.AsNoTracking(),
                        candidate => candidate.RealmId,
                        realm => realm.Id,
                        (candidate, realm) => new { Role = candidate, Realm = realm })
                    .Where(x => x.Role.TenantId == tenant.TenantId && x.Role.Name == roleAssignment.Role.Trim() && x.Realm.Name == realmName)
                    .Select(x => x.Role)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (role is null)
                {
                    logger.LogWarning(
                        "Seed manifest user {Username} references missing role {RoleName} in realm {RealmName} for tenant {TenantSlug}.",
                        username,
                        roleAssignment.Role,
                        realmName,
                        tenant.Slug);
                    continue;
                }

                var existingRoleAssignment = await db.UserRealmRoleAssignments.FirstOrDefaultAsync(
                        a => a.UserId == user.Id && a.RoleId == role.Id && a.RealmId == role.RealmId,
                        ct)
                    .ConfigureAwait(false);

                if (existingRoleAssignment is null)
                {
                    db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
                    {
                        UserId = user.Id,
                        RoleId = role.Id,
                        RealmId = role.RealmId,
                        IsActive = true
                    });
                }
                else if (seedOptions.Value.AllowUpdates && !existingRoleAssignment.IsActive)
                {
                    existingRoleAssignment.IsActive = true;
                }
            }

            foreach (var clientAssignment in userDef.Clients)
            {
                if (string.IsNullOrWhiteSpace(clientAssignment.ClientId))
                {
                    continue;
                }

                var requestedClientId = clientAssignment.ClientId.Trim();
                var requestedRealmName = string.IsNullOrWhiteSpace(clientAssignment.Realm) ? null : clientAssignment.Realm.Trim();

                var client = await db.Clients
                    .AsNoTracking()
                    .Join(
                        db.Realms.AsNoTracking(),
                        candidate => candidate.RealmId,
                        realm => realm.Id,
                        (candidate, realm) => new { Client = candidate, Realm = realm })
                    .Where(x => x.Client.TenantId == tenant.TenantId && x.Client.ClientId == requestedClientId)
                    .Where(x => requestedRealmName == null || x.Realm.Name == requestedRealmName)
                    .Select(x => x.Client)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (client is null)
                {
                    logger.LogWarning(
                        "Seed manifest user {Username} references missing client {ClientId} for tenant {TenantSlug}.",
                        username,
                        requestedClientId,
                        tenant.Slug);
                    continue;
                }

                var existingClientAssignment = await db.UserClientAssignments.FirstOrDefaultAsync(
                        a => a.UserId == user.Id && a.ClientId == client.Id && a.RealmId == client.RealmId,
                        ct)
                    .ConfigureAwait(false);

                if (existingClientAssignment is null)
                {
                    db.UserClientAssignments.Add(new UserClientAssignment
                    {
                        UserId = user.Id,
                        ClientId = client.Id,
                        RealmId = client.RealmId,
                        IsActive = true
                    });
                }
                else if (seedOptions.Value.AllowUpdates && !existingClientAssignment.IsActive)
                {
                    existingClientAssignment.IsActive = true;
                }
            }

            if (db.ChangeTracker.HasChanges())
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private static AutoApprovalMode ParseAutoApprovalMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AutoApprovalMode.No;

        return Enum.TryParse<AutoApprovalMode>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : AutoApprovalMode.No;
    }

    private static string? ResolveClientSecret(ClientSeedDefinition def, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(def.ClientSecret))
        {
            return def.ClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(def.ClientSecretEnv))
        {
            var key = def.ClientSecretEnv.Trim();
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private string? ResolveUserPassword(UserSeedDefinition def)
    {
        if (!string.IsNullOrWhiteSpace(def.Password))
        {
            return def.Password;
        }

        if (!string.IsNullOrWhiteSpace(def.PasswordEnv))
        {
            var key = def.PasswordEnv.Trim();
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
