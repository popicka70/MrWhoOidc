using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.Auth.Services;

public interface ISeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public sealed class Seeder(AuthDbContext db, IPasswordHasher hasher) : ISeeder
{
    // Initial constant secret for the blazor-web client (development only)
    private const string InitialBlazorWebClientSecret = "z1bvxwNcBXeOP03EMUdawfHnBhx6KAXuYArRSY6a1ZPyme7JMJ_A50bQY75FW6TG";

    // PoC M2M client id/secret (intentionally hard-coded)
    private const string M2MClientId = "m2m-test-client";
    private const string M2MClientSecret = "m2m-test-secret";

    // Admin seeded identities
    private const string AdminUsername = "admin";
    private const string AdminEmail = "admin@mrwho.local";
    private const string AdminEasyPassword = "Admin123!"; // dev-only, change in production

    // Admin client (used to model server management policies)
    private const string AdminClientId = "mrwho-admin";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Ensure admin realm exists
        var adminRealm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "admin", ct).ConfigureAwait(false);
        if (adminRealm is null)
        {
            adminRealm = new Realm { Name = "admin", DisplayName = "Admin Realm" };
            db.Realms.Add(adminRealm);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Seed default scopes
        string[] defaultScopes = ["openid", "profile", "email", "offline_access", "roles"];
        foreach (var s in defaultScopes)
        {
            if (!await db.Scopes.AnyAsync(x => x.Name == s, ct).ConfigureAwait(false))
            {
                db.Scopes.Add(new Scope { Name = s, Description = $"Standard scope {s}", IsExposed = true });
            }
        }

        // Seed an admin role in admin realm
        if (!await db.Roles.AnyAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin", ct).ConfigureAwait(false))
        {
            db.Roles.Add(new Role { Name = "admin", RealmId = adminRealm.Id, IsActive = true });
        }

        // Seed demo user alice if DB is empty (kept for compatibility)
        if (!await db.Users.AnyAsync(ct).ConfigureAwait(false))
        {
            db.Users.Add(new User
            {
                Username = "alice",
                PasswordHash = hasher.Hash("P@ssw0rd!"),
                HashAlgorithm = "argon2id",
                Name = "Alice Adams",
                Email = "alice@example.com",
                EmailVerified = true,
                EmailVerifiedAt = DateTimeOffset.UtcNow
            });
        }

        // Seed default admin user (idempotent)
    var normalizedAdminEmail = EmailNormalizer.NormalizeForLookup(AdminEmail);
    var adminUser = await db.Users.FirstOrDefaultAsync(u => u.Username == AdminUsername || u.NormalizedEmail == normalizedAdminEmail, ct).ConfigureAwait(false);
        if (adminUser is null)
        {
            adminUser = new User
            {
                Username = AdminUsername,
                Name = "System Administrator",
                Email = AdminEmail,
                EmailVerified = true,
                EmailVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = hasher.Hash(AdminEasyPassword),
                HashAlgorithm = "argon2id"
            };
            db.Users.Add(adminUser);
        }

        // Ensure blazor-web client exists as a confidential client with an initial constant secret
        var blazorWebClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == "blazor-web", ct).ConfigureAwait(false);
        if (blazorWebClient is null)
        {
            blazorWebClient = new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = hasher.Hash(InitialBlazorWebClientSecret),
                RealmId = adminRealm.Id,
                IntrospectionAudiencesJson = JsonSerializer.Serialize(new[] { "api" })
            };
            db.Clients.Add(blazorWebClient);
        }
        else
        {
            if (string.IsNullOrEmpty(blazorWebClient.ClientSecretHash))
            {
                // Backfill a secret if previously created as public client
                blazorWebClient.ClientSecretHash = hasher.Hash(InitialBlazorWebClientSecret);
                blazorWebClient.RequirePkce = true;
            }
            if (string.IsNullOrEmpty(blazorWebClient.IntrospectionAudiencesJson))
            {
                // Enable introspection against default API audience
                blazorWebClient.IntrospectionAudiencesJson = JsonSerializer.Serialize(new[] { "api" });
            }
        }

        // Seed dedicated admin client (separate from demo blazor-web)
        var adminClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == AdminClientId, ct).ConfigureAwait(false);
        if (adminClient is null)
        {
            adminClient = new Client
            {
                ClientId = AdminClientId,
                ClientName = "MrWho Admin",
                RequirePkce = true,
                RequireConsent = false,
                // Keep as public by default for interactive code flow with PKCE
                ClientSecretHash = null,
                RealmId = adminRealm.Id,
                // Admin portal typically needs roles scope
                AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://localhost:5003/signin-oidc", "http://localhost:5003/signin-oidc" })
            };
            db.Clients.Add(adminClient);
        }

        // Seed a simple M2M confidential client (client_credentials)
        var m2m = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == M2MClientId, ct).ConfigureAwait(false);
        if (m2m is null)
        {
            m2m = new Client
            {
                ClientId = M2MClientId,
                ClientName = "M2M Test Client",
                RequirePkce = false,
                RequireConsent = false,
                ClientSecretHash = hasher.Hash(M2MClientSecret),
                RealmId = adminRealm.Id
            };
            db.Clients.Add(m2m);
        }
        else if (string.IsNullOrEmpty(m2m.ClientSecretHash))
        {
            // Backfill a secret if missing
            m2m.ClientSecretHash = hasher.Hash(M2MClientSecret);
        }

        // Backfill RealmId for any existing client rows missing it
        var clientsWithoutRealm = await db.Clients.Where(c => c.RealmId == Guid.Empty).ToListAsync(ct).ConfigureAwait(false);
        foreach (var c in clientsWithoutRealm)
        {
            c.RealmId = adminRealm.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Assign default standard scopes to blazor-web client if none exist
        var existingClientScopes = await db.ClientScopes.Where(cs => cs.ClientId == blazorWebClient.Id).Select(cs => cs.ScopeName).ToListAsync(ct).ConfigureAwait(false);
        if (existingClientScopes.Count == 0)
        {
            foreach (var s in defaultScopes)
            {
                db.ClientScopes.Add(new ClientScope { ClientId = blazorWebClient.Id, ScopeName = s });
            }
        }

        // Assign default scopes to admin client as well
        var adminClientScopes = await db.ClientScopes.Where(cs => cs.ClientId == adminClient.Id).Select(cs => cs.ScopeName).ToListAsync(ct).ConfigureAwait(false);
        if (adminClientScopes.Count == 0)
        {
            foreach (var s in defaultScopes)
            {
                db.ClientScopes.Add(new ClientScope { ClientId = adminClient.Id, ScopeName = s });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Optionally assign alice to blazor-web client in admin realm
        var alice = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == "alice", ct).ConfigureAwait(false);
        if (alice is not null)
        {
            var hasAssignment = await db.UserClientAssignments.AnyAsync(a => a.UserId == alice.Id && a.ClientId == blazorWebClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasAssignment)
            {
                db.UserClientAssignments.Add(new UserClientAssignment { UserId = alice.Id, ClientId = blazorWebClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }

            // Assign admin role to alice for blazor-web in admin realm (demo)
            var adminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin", ct).ConfigureAwait(false);
            var hasRole = await db.UserRoleAssignments.AnyAsync(a => a.UserId == alice.Id && a.RoleId == adminRole.Id && a.ClientId == blazorWebClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasRole)
            {
                db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = alice.Id, RoleId = adminRole.Id, ClientId = blazorWebClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }
        }

        // Ensure admin user has client assignment and admin role in admin realm
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

            // Role assignment
            var adminRole = await db.Roles.AsNoTracking().FirstAsync(r => r.RealmId == adminRealm.Id && r.Name == "admin", ct).ConfigureAwait(false);
            var hasAdminRole = await db.UserRoleAssignments.AnyAsync(a => a.UserId == adminUser.Id && a.RoleId == adminRole.Id && a.ClientId == adminClient.Id && a.RealmId == adminRealm.Id, ct).ConfigureAwait(false);
            if (!hasAdminRole)
            {
                db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = adminUser.Id, RoleId = adminRole.Id, ClientId = adminClient.Id, RealmId = adminRealm.Id, IsActive = true });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Seed a sample external OIDC Identity Provider and map it to the blazor-web client (if IdP tables exist)
        try
        {
            // Skip if already present
            var existingIdp = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == "dev-oidc", ct).ConfigureAwait(false);
            if (existingIdp is null)
            {
                var config = new
                {
                    Authority = "https://login.example.com/",
                    ClientId = "mrwho-webauth-dev",
                    ClientSecret = "<replace-in-prod>",
                    ResponseType = "code",
                    Scopes = new[] { "openid", "profile", "email" },
                    UsePKCE = true,
                    UseJAR = false,
                    UsePAR = false,
                    RequestedAcrValues = (string?)null,
                    Prompt = (string?)null,
                    ResponseMode = (string?)null,
                    ClockSkewSeconds = 120,
                    TokenValidation = new { ValidateIssuer = true, ValidateAudience = false, ValidateLifetime = true },
                    BackChannelLogout = true,
                    ExtraAuthParams = new { }
                };

                var idp = new IdentityProvider
                {
                    Name = "dev-oidc",
                    DisplayName = "Dev OIDC",
                    Type = IdentityProviderType.Oidc,
                    Enabled = true,
                    IsDefault = false,
                    LogoUrl = null,
                    SortOrder = 0,
                    ConfigJson = JsonSerializer.Serialize(config),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.IdentityProviders.Add(idp);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                // Map to blazor-web client
                var cip = new ClientIdentityProvider
                {
                    ClientId = blazorWebClient.Id,
                    IdentityProviderId = idp.Id,
                    Enabled = true,
                    IsDefaultForClient = true,
                    AutoRedirectIfSingle = false,
                    RequiredAcr = null,
                    Order = 0
                };
                db.ClientIdentityProviders.Add(cip);

                // Basic claim mappings
                db.IdentityProviderClaimMappings.AddRange(
                    new IdentityProviderClaimMapping { IdentityProviderId = idp.Id, ExternalClaim = "sub", LocalClaim = "sub", Order = 0 },
                    new IdentityProviderClaimMapping { IdentityProviderId = idp.Id, ExternalClaim = "email", LocalClaim = "email", Order = 1 },
                    new IdentityProviderClaimMapping { IdentityProviderId = idp.Id, ExternalClaim = "name", LocalClaim = "name", Order = 2 }
                );

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // If the IdP tables are not yet migrated, ignore seeding to keep startup resilient.
        }
    }
}
