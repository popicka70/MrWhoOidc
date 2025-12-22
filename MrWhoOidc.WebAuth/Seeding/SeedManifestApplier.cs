using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Seeding;

public interface ISeedManifestApplier
{
    Task ApplyTenantsAsync(SeedManifest manifest, string authorityBaseUrl, CancellationToken ct = default);
    Task ApplyForCurrentTenantAsync(SeedManifest manifest, CancellationToken ct = default);
}

internal sealed class SeedManifestApplier(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IIssuerBuilder issuerBuilder,
    IOptions<OidcOptions> oidcOptions,
    IOptions<SeedManifestOptions> seedOptions,
    IConfiguration configuration,
    IPasswordHasher passwordHasher,
    IClientStore clientStore,
    ILogger<SeedManifestApplier> logger) : ISeedManifestApplier
{
    public async Task ApplyTenantsAsync(SeedManifest manifest, string authorityBaseUrl, CancellationToken ct = default)
    {
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
                    ClientSecretHash = string.IsNullOrWhiteSpace(resolvedSecret) ? null : passwordHasher.Hash(resolvedSecret)
                };
#pragma warning restore CS0618

                ApplyRedirectUris(client, clientDef);

                ApplyOboPolicy(client, clientDef, allowUpdates: true);

                db.Clients.Add(client);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

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
                    client.RequirePkce = true;
                }
            }
#pragma warning restore CS0618

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await clientStore.InvalidateClientCacheAsync(client.ClientId, tenant.TenantId, ct).ConfigureAwait(false);

            await EnsureSeededAdminHasAdminRoleForClientAsync(client, ct).ConfigureAwait(false);
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
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Username == "admin", ct)
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

        var hasRoleAssignment = await db.UserRoleAssignments.AnyAsync(
            a => a.UserId == adminUser.Id && a.RoleId == adminRole.Id && a.ClientId == client.Id && a.RealmId == adminRealm.Id && a.IsActive,
            ct).ConfigureAwait(false);

        if (!hasRoleAssignment)
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                ClientId = client.Id,
                RealmId = adminRealm.Id,
                IsActive = true
            });
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
}
