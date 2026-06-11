using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Linq;
using System.Text.Json;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace MrWhoOidc.Auth.Services;

public interface ISeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public sealed class Seeder(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor, IUserAccountProvisioner accountProvisioner, IHostEnvironment env, ILogger<Seeder> logger) : ISeeder
{
    // PoC M2M client id (secret is resolved at runtime, never hard-coded)
    private const string M2MClientId = "m2m-test-client";

    // Example downstream API client for on-behalf-of demo
    private const string TestApiClientId = "test-api";

    // Admin seeded identities
    private const string AdminUsername = "admin";
    private const string AdminEmail = "admin@mrwho.local";

    // Admin client (used to model server management policies)
    private const string AdminClientId = "mrwho-admin";
    private const string LicensingAdminClientId = "licensing-admin";
    private const string ReactDemoClientId = "react-demo";

    private readonly IUserAccountProvisioner _accountProvisioner = accountProvisioner;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Get current tenant ID from context (required for multi-tenancy)
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required for seeding");

        // Resolve confidential client secrets at runtime. Secrets are taken from the
        // environment when provided, otherwise a cryptographically random secret is
        // generated per deployment. Secrets are never hard-coded or logged so that a
        // freshly bootstrapped instance does not ship with publicly known credentials
        // baked into the source, image, or log stream.
        var blazorWebSecret = GetClientSecret("SEED_BLAZOR_WEB_CLIENT_SECRET", "blazor-web");
        var m2mSecret = GetClientSecret("SEED_M2M_CLIENT_SECRET", M2MClientId);
        var testApiSecret = GetClientSecret("SEED_TEST_API_CLIENT_SECRET", TestApiClientId);

        // Ensure admin realm exists
        var adminRealm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
        if (adminRealm is null)
        {
            adminRealm = new Realm { Name = "admin", DisplayName = "Admin Realm", TenantId = tenantId };
            db.Realms.Add(adminRealm);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Ensure default realm exists (for tenant-admin role and tenant-specific config)
        var defaultRealm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "default" && r.TenantId == tenantId, ct).ConfigureAwait(false);
        if (defaultRealm is null)
        {
            defaultRealm = new Realm { Name = "default", DisplayName = "Default Realm", TenantId = tenantId };
            db.Realms.Add(defaultRealm);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Ensure platform realm exists (for platform administrators)
        var platformRealm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "platform" && r.TenantId == tenantId, ct).ConfigureAwait(false);
        if (platformRealm is null)
        {
            platformRealm = new Realm { Name = "platform", DisplayName = "Platform Admin Realm", TenantId = tenantId };
            db.Realms.Add(platformRealm);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Seed default scopes (scopes are global, not tenant-specific)
        // Note: presence in this table does NOT automatically grant the scope to clients.
        string[] defaultScopes = ["openid", "profile", "email", "offline_access", "roles", "api.read", OidcConstants.Scopes.Tenants];
        foreach (var s in defaultScopes)
        {
            if (!await db.Scopes.AnyAsync(x => x.Name == s, ct).ConfigureAwait(false))
            {
                db.Scopes.Add(new Scope
                {
                    Name = s,
                    Description = $"Standard scope {s}",
                    IsExposed = true,
                    IsGlobal = true,
                    TenantId = null
                });
            }
        }

        // Save scopes immediately to avoid race conditions in parallel tests
        try
        {
            if (db.ChangeTracker.HasChanges())
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (ArgumentException ex) when (ex.Message.Contains("An item with the same key has already been added"))
        {
            // Ignore duplicate key errors for scopes (can happen in parallel test execution)
            // Clear the tracked entities that failed to save
            db.ChangeTracker.Clear();
        }

        // Seed admin roles in admin realm
        if (!await db.Roles.AnyAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin" && r.TenantId == tenantId, ct).ConfigureAwait(false))
        {
            db.Roles.Add(new Role { Name = "admin", RealmId = adminRealm.Id, IsActive = true, TenantId = tenantId });
        }

        if (!await db.Roles.AnyAsync(r => r.RealmId == adminRealm.Id && r.Name == "licensing-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false))
        {
            db.Roles.Add(new Role { Name = "licensing-admin", RealmId = adminRealm.Id, IsActive = true, TenantId = tenantId });
        }

        // Seed tenant-admin role in default realm (for Tenant Admin UI access)
        if (!await db.Roles.AnyAsync(r => r.RealmId == defaultRealm.Id && r.Name == "tenant-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false))
        {
            db.Roles.Add(new Role { Name = "tenant-admin", RealmId = defaultRealm.Id, IsActive = true, TenantId = tenantId });
        }

        // Seed platform-admin role in platform realm (for Platform Admin UI access)
        if (!await db.Roles.AnyAsync(r => r.RealmId == platformRealm.Id && r.Name == "platform-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false))
        {
            db.Roles.Add(new Role { Name = "platform-admin", RealmId = platformRealm.Id, IsActive = true, TenantId = tenantId });
        }

        // Seed demo user "alice" (well-known password) ONLY in development/staging.
        // SeedAsync also runs from the production /bootstrap endpoint, which executes in any
        // environment; seeding alice there would ship a verified, admin-role login with a
        // source-published password ("P@ssw0rd!"). Essential identities (admin user, realms,
        // roles, scopes) are seeded unconditionally elsewhere in this method.
        var seedDemoIdentities = env.IsDevelopment() || env.IsStaging();
        if (seedDemoIdentities && !await db.Users.AnyAsync(u => u.TenantId == tenantId, ct).ConfigureAwait(false))
        {
            var seededAlice = new User
            {
                Username = "alice",
                Name = "Alice Adams",
                Email = "alice@example.com",
                EmailVerified = true,
                EmailVerifiedAt = DateTimeOffset.UtcNow,
                TenantId = tenantId
            };
            db.Users.Add(seededAlice);
            await _accountProvisioner.EnsureAsync(seededAlice, tenantId, adminRealm.Id, isTenantAdmin: false, ct).ConfigureAwait(false);

            // Set password on the UserAccount (global credentials)
            var aliceAccount = await db.UserAccounts.FirstOrDefaultAsync(a => a.Username == "alice", ct).ConfigureAwait(false);
            if (aliceAccount != null)
            {
                aliceAccount.PasswordHash = hasher.Hash("P@ssw0rd!");
                aliceAccount.HashAlgorithm = "argon2id";
            }
        }

        // Seed default admin user (idempotent)
        var normalizedAdminEmail = EmailNormalizer.NormalizeForLookup(AdminEmail);
        var adminUser = await db.Users.FirstOrDefaultAsync(u => (u.Username == AdminUsername || u.NormalizedEmail == normalizedAdminEmail) && u.TenantId == tenantId, ct).ConfigureAwait(false);
        if (adminUser is null)
        {
            adminUser = new User
            {
                Username = AdminUsername,
                Name = "System Administrator",
                Email = AdminEmail,
                EmailVerified = true,
                EmailVerifiedAt = DateTimeOffset.UtcNow,
                TenantId = tenantId
            };
            db.Users.Add(adminUser);
        }

        if (adminUser is not null)
        {
            await _accountProvisioner.EnsureAsync(adminUser, tenantId, adminRealm.Id, isTenantAdmin: true, ct).ConfigureAwait(false);

            // Set password on the UserAccount (global credentials)
            var adminAccount = await db.UserAccounts.FirstOrDefaultAsync(a => a.Username == AdminUsername, ct).ConfigureAwait(false);
            if (adminAccount != null && RequiresSeededAdminPassword(adminAccount.PasswordHash))
            {
                var password = GetAdminPassword();
                adminAccount.PasswordHash = hasher.Hash(password);
                adminAccount.HashAlgorithm = "argon2id";
                logger.LogWarning(
                    "Auto-seeded admin password was generated for {Username}. The value is not logged; set SEED_ADMIN_PASSWORD explicitly for operator-controlled bootstrap credentials.",
                    AdminUsername);
            }
        }

        // Ensure blazor-web client exists as a confidential client with an initial constant secret
        var blazorWebClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == "blazor-web" && c.TenantId == tenantId, ct).ConfigureAwait(false);
        if (blazorWebClient == null)
        {
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            blazorWebClient = new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = hasher.Hash(blazorWebSecret),
                RealmId = adminRealm.Id,
                TenantId = tenantId,
                IntrospectionAudiencesJson = JsonSerializer.Serialize(new[] { "api" }),
                AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] {
                    "https://localhost:7181/signin-oidc",
                    "http://localhost:7181/signin-oidc",
                    "https://localhost:5001/signin-oidc",
                    "http://localhost:5001/signin-oidc",
                    "https://localhost:5003/Auth/Callback",
                    "http://localhost:5002/Auth/Callback"
                }),
                AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(new[] {
                    "https://localhost:7181/signout-callback-oidc",
                    "https://localhost:7181/",
                    "http://localhost:7181/signout-callback-oidc",
                    "http://localhost:7181/",
                    "https://localhost:5001/signout-callback-oidc",
                    "https://localhost:5001/",
                    "http://localhost:5001/signout-callback-oidc",
                    "http://localhost:5001/",
                    "https://localhost:5003/",
                    "http://localhost:5002/"
                }),
                OboEnabled = true,
                OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api" }),
                OboAllowedScopesJson = JsonSerializer.Serialize(new[] { "api.read" }),
                OboMaxDelegationDepth = 1,
                OboMaxLifetimeMinutes = 15
            };
            db.Clients.Add(blazorWebClient);
        }
        else
        {
            if (string.IsNullOrEmpty(blazorWebClient.ClientSecretHash))
            {
                // Backfill a secret if previously created as public client
                blazorWebClient.ClientSecretHash = hasher.Hash(blazorWebSecret);
                blazorWebClient.RequirePkce = true;
            }
#pragma warning restore CS0618
            if (string.IsNullOrEmpty(blazorWebClient.IntrospectionAudiencesJson))
            {
                // Enable introspection against default API audience
                blazorWebClient.IntrospectionAudiencesJson = JsonSerializer.Serialize(new[] { "api" });
            }

            // Backfill redirect URIs if missing
            if (string.IsNullOrEmpty(blazorWebClient.AllowedLoginRedirectUrisJson))
            {
                blazorWebClient.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] {
                    "https://localhost:7181/signin-oidc",
                    "http://localhost:7181/signin-oidc",
                    "https://localhost:5001/signin-oidc",
                    "http://localhost:5001/signin-oidc",
                    "https://localhost:5003/Auth/Callback",
                    "http://localhost:5002/Auth/Callback"
                });
            }
            if (string.IsNullOrEmpty(blazorWebClient.AllowedLogoutRedirectUrisJson))
            {
                blazorWebClient.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(new[] {
                    "https://localhost:7181/signout-callback-oidc",
                    "https://localhost:7181/",
                    "http://localhost:7181/signout-callback-oidc",
                    "http://localhost:7181/",
                    "https://localhost:5001/signout-callback-oidc",
                    "https://localhost:5001/",
                    "http://localhost:5001/signout-callback-oidc",
                    "http://localhost:5001/",
                    "https://localhost:5003/",
                    "http://localhost:5002/"
                });
            }

            // Enable on-behalf-of for the demo Razor client
            blazorWebClient.OboEnabled ??= true;
            if (string.IsNullOrEmpty(blazorWebClient.OboAllowedTargetAudiencesJson))
            {
                blazorWebClient.OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api" });
            }
            if (string.IsNullOrEmpty(blazorWebClient.OboAllowedScopesJson))
            {
                blazorWebClient.OboAllowedScopesJson = JsonSerializer.Serialize(new[] { "api.read" });
            }
            blazorWebClient.OboMaxDelegationDepth ??= 1;
            blazorWebClient.OboMaxLifetimeMinutes ??= 15;
        }

        // Seed dedicated admin client (separate from demo blazor-web)
        var adminClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == AdminClientId && c.TenantId == tenantId, ct).ConfigureAwait(false);
        if (adminClient is null)
        {
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            adminClient = new Client
            {
                ClientId = AdminClientId,
                ClientName = "MrWho Admin",
                RequirePkce = true,
                RequireConsent = false,
                // Keep as public by default for interactive code flow with PKCE
                ClientSecretHash = null,
                RealmId = adminRealm.Id,
                TenantId = tenantId,
                // Admin portal typically needs roles scope
                AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://localhost:5003/signin-oidc", "http://localhost:5003/signin-oidc" })
            };
            db.Clients.Add(adminClient);
        }

        var reactDemoClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == ReactDemoClientId && c.TenantId == tenantId, ct).ConfigureAwait(false);
        if (reactDemoClient is null)
        {
            reactDemoClient = new Client
            {
                ClientId = ReactDemoClientId,
                ClientName = "React OIDC Demo",
                RequirePkce = true,
                RequireConsent = false,
                ClientSecretHash = null,
                RealmId = adminRealm.Id,
                TenantId = tenantId,
                ApplicationType = "spa",
                AllowLocalLogin = true,
                AllowExternalIdp = true,
                AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[]
                {
                    "http://localhost:5173/callback"
                }),
                AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(new[]
                {
                    "http://localhost:5173/"
                })
            };
            db.Clients.Add(reactDemoClient);
        }
        else
        {
            reactDemoClient.RequirePkce = true;
            reactDemoClient.RequireConsent = false;
            reactDemoClient.ClientSecretHash = null;
            reactDemoClient.RequirePar = false;
            reactDemoClient.ApplicationType = "spa";
            reactDemoClient.AllowLocalLogin = true;
            reactDemoClient.AllowExternalIdp = true;
            reactDemoClient.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[]
            {
                "http://localhost:5173/callback"
            });
            reactDemoClient.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(new[]
            {
                "http://localhost:5173/"
            });
        }

        // Seed a simple M2M confidential client (client_credentials)
        var m2m = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == M2MClientId && c.TenantId == tenantId, ct).ConfigureAwait(false);
        if (m2m is null)
        {
            m2m = new Client
            {
                ClientId = M2MClientId,
                ClientName = "M2M Test Client",
                RequirePkce = false,
                RequireConsent = false,
                ClientSecretHash = hasher.Hash(m2mSecret),
                RealmId = adminRealm.Id,
                TenantId = tenantId
            };
            db.Clients.Add(m2m);
        }
        else if (string.IsNullOrEmpty(m2m.ClientSecretHash))
        {
            // Backfill a secret if missing
            m2m.ClientSecretHash = hasher.Hash(m2mSecret);
        }
#pragma warning restore CS0618

        // Backfill RealmId for any existing client rows missing it (within current tenant)
        var clientsWithoutRealm = await db.Clients.Where(c => c.TenantId == tenantId && c.RealmId == Guid.Empty).ToListAsync(ct).ConfigureAwait(false);
        foreach (var c in clientsWithoutRealm)
        {
            c.RealmId = adminRealm.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Seed example API confidential client (used for demonstrations and validation)
        var testApiClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == TestApiClientId && c.TenantId == tenantId, ct).ConfigureAwait(false);
        if (testApiClient is null)
        {
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            testApiClient = new Client
            {
                ClientId = TestApiClientId,
                ClientName = "Examples Test API",
                RequirePkce = false,
                RequireConsent = false,
                ClientSecretHash = hasher.Hash(testApiSecret),
                RealmId = adminRealm.Id,
                TenantId = tenantId,
                IntrospectionAudiencesJson = JsonSerializer.Serialize(new[] { "api" })
            };
            db.Clients.Add(testApiClient);
        }
        else if (string.IsNullOrEmpty(testApiClient.ClientSecretHash))
        {
            testApiClient.ClientSecretHash = hasher.Hash(testApiSecret);
        }
#pragma warning restore CS0618

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Assign default standard scopes to blazor-web client if none exist
        var existingClientScopes = await db.ClientScopes.Where(cs => cs.ClientId == blazorWebClient.Id).Select(cs => cs.ScopeName).ToListAsync(ct).ConfigureAwait(false);
        foreach (var scope in defaultScopes.Except(existingClientScopes, StringComparer.Ordinal))
        {
            db.ClientScopes.Add(new ClientScope { ClientId = blazorWebClient.Id, ScopeName = scope });
        }

        // Assign default scopes to admin client as well
        var adminClientScopes = await db.ClientScopes.Where(cs => cs.ClientId == adminClient.Id).Select(cs => cs.ScopeName).ToListAsync(ct).ConfigureAwait(false);
        foreach (var scope in defaultScopes.Except(adminClientScopes, StringComparer.Ordinal))
        {
            db.ClientScopes.Add(new ClientScope { ClientId = adminClient.Id, ScopeName = scope });
        }

        var reactClientScopes = await db.ClientScopes.Where(cs => cs.ClientId == reactDemoClient.Id).Select(cs => cs.ScopeName).ToListAsync(ct).ConfigureAwait(false);
        foreach (var scope in defaultScopes.Except(reactClientScopes, StringComparer.Ordinal))
        {
            db.ClientScopes.Add(new ClientScope { ClientId = reactDemoClient.Id, ScopeName = scope });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Optionally assign alice to blazor-web client in admin realm
        var alice = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == "alice" && u.TenantId == tenantId, ct).ConfigureAwait(false);
        if (alice is not null)
        {
            await _accountProvisioner.EnsureAsync(alice, tenantId, adminRealm.Id, isTenantAdmin: false, ct).ConfigureAwait(false);

            var hasAssignment = await db.UserClientAssignments.AnyAsync(a => a.UserId == alice.Id && a.ClientId == blazorWebClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasAssignment)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = alice.Id, ClientId = blazorWebClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            var hasReactAssignment = await db.UserClientAssignments.AnyAsync(a => a.UserId == alice.Id && a.ClientId == reactDemoClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasReactAssignment)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = alice.Id, ClientId = reactDemoClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            // Assign admin role to alice in admin realm (realm-scoped)
            var adminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
            var hasRole = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == alice.Id && a.RoleId == adminRole.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasRole)
            {
                db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = alice.Id, RoleId = adminRole.Id, RealmId = adminRealm.Id, IsActive = true });
            }

        }

        // Ensure admin user has client assignment and admin roles
        if (adminUser is not null)
        {
            // Assignment to admin client
            var adminAssigned = await db.UserClientAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.ClientId == adminClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!adminAssigned)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = adminUser.Id, ClientId = adminClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            // Also assignment to blazor-web for convenience
            var adminAssignedToBlazor = await db.UserClientAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.ClientId == blazorWebClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!adminAssignedToBlazor)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = adminUser.Id, ClientId = blazorWebClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            var adminAssignedToReact = await db.UserClientAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.ClientId == reactDemoClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!adminAssignedToReact)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = adminUser.Id, ClientId = reactDemoClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            var licensingAdminClient = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == LicensingAdminClientId && c.TenantId == tenantId && c.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (licensingAdminClient is not null)
            {
                var adminAssignedToLicensingAdmin = await db.UserClientAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.ClientId == licensingAdminClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
                if (!adminAssignedToLicensingAdmin)
                {
                    db.UserClientAssignments.Add(new UserClientAssignment { UserId = adminUser.Id, ClientId = licensingAdminClient.Id, RealmId = adminRealm.Id, IsActive = true });
                }
            }

            // Admin role assignment in admin realm (realm-scoped)
            var adminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
            var hasAdminRole = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.RoleId == adminRole.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasAdminRole)
            {
                db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = adminUser.Id, RoleId = adminRole.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            // Licensing backoffice role assignment in admin realm (realm-scoped)
            var licensingAdminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == adminRealm.Id && r.Name == "licensing-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
            var hasLicensingAdminRole = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.RoleId == licensingAdminRole.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasLicensingAdminRole)
            {
                db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = adminUser.Id, RoleId = licensingAdminRole.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            // Tenant-admin role assignment in default realm (realm-scoped)
            var tenantAdminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == defaultRealm.Id && r.Name == "tenant-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
            var hasTenantAdminRole = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.RoleId == tenantAdminRole.Id && a.RealmId == defaultRealm.Id, ct).ConfigureAwait(false);
            if (!hasTenantAdminRole)
            {
                db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = adminUser.Id, RoleId = tenantAdminRole.Id, RealmId = defaultRealm.Id, IsActive = true });
            }

            // Platform admin role assignment in platform realm (realm-scoped)
            var platformAdminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == platformRealm.Id && r.Name == "platform-admin" && r.TenantId == tenantId, ct).ConfigureAwait(false);
            var hasPlatformAdminRole = await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.RoleId == platformAdminRole.Id && a.RealmId == platformRealm.Id, ct).ConfigureAwait(false);
            if (!hasPlatformAdminRole)
            {
                db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = adminUser.Id, RoleId = platformAdminRole.Id, RealmId = platformRealm.Id, IsActive = true });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

    }

    private static string GetAdminPassword()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        const string choices = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        return RandomNumberGenerator.GetString(choices, 20);
    }

    // Resolves a confidential client secret from the environment, or generates a
    // cryptographically random one. Never returns a hard-coded value or logs the secret.
    private string GetClientSecret(string envVar, string clientId)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        var generated = RandomNumberGenerator.GetString(
            "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 48);
        logger.LogWarning(
            "Auto-generated secret for seeded client '{ClientId}' was created and hashed. The value is not logged; set {EnvVar} to use an operator-controlled value.",
            clientId, envVar);
        return generated;
    }

    private static bool RequiresSeededAdminPassword(string? passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return true;
        }

        if (passwordHash.StartsWith("v2:Isopoh.Cryptography.", StringComparison.Ordinal))
        {
            return true;
        }

        return passwordHash.StartsWith("v2:", StringComparison.Ordinal)
            && !passwordHash[3..].StartsWith("$argon2", StringComparison.Ordinal);
    }
}
