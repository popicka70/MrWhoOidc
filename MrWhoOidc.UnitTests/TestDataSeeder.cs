using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Helper to create and seed an in-memory AuthDbContext for tests that need richer data.
/// </summary>
public static class TestDataSeeder
{
    public static AuthDbContext CreateInMemoryDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Seed a default realm, users, scopes, roles, two clients (spa + conf), assignments and consents.
    /// Returns a strongly-typed handle to the created entities.
    /// </summary>
    public static async Task<SeedData> SeedBasicAsync(AuthDbContext db, bool hashClientSecret = true)
    {
        // Realms
        var realm = new Realm { Name = "default", DisplayName = "Default" };
        db.Realms.Add(realm);

        // Scopes
        var scopes = new[]
        {
            new Scope { Name = "openid", Description = "OpenID Connect" },
            new Scope { Name = "profile", Description = "Basic profile" },
            new Scope { Name = "email", Description = "Email" },
            new Scope { Name = "offline_access", Description = "Refresh tokens" },
            new Scope { Name = "roles", Description = "Role claims" }
        };
        db.Scopes.AddRange(scopes);

        // Roles
        var roleAdmin = new Role { Name = "admin", RealmId = realm.Id };
        var roleUser = new Role { Name = "user", RealmId = realm.Id };
        db.Roles.AddRange(roleAdmin, roleUser);

        // Users
        var alice = new User { Username = "alice", Email = "alice@example.com", Name = "Alice" };
        var bob = new User { Username = "bob", Email = "bob@example.com", Name = "Bob" };
        db.Users.AddRange(alice, bob);

        // Clients
        var spa = new ClientEntity
        {
            ClientId = "spa",
            ClientName = "SPA Client",
            RealmId = realm.Id,
            RequirePkce = true,
            RequireConsent = true,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app.example.com/callback", "https://app.example.com/oidc-cb" })
        };

        var conf = new ClientEntity
        {
            ClientId = "conf",
            ClientName = "Confidential Client",
            RealmId = realm.Id,
            RequirePkce = false,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://conf.example.com/cb" })
        };

        if (hashClientSecret)
        {
            var hasher = new Argon2PasswordHasher();
            conf.ClientSecretHash = hasher.Hash("secret");
        }

        db.Clients.AddRange(spa, conf);

        // Client scopes
        foreach (var s in scopes)
        {
            db.ClientScopes.Add(new ClientScope { ClientId = spa.Id, ScopeName = s.Name });
            db.ClientScopes.Add(new ClientScope { ClientId = conf.Id, ScopeName = s.Name });
        }

        // Assignments
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = alice.Id, ClientId = spa.Id, RealmId = realm.Id });
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = alice.Id, ClientId = conf.Id, RealmId = realm.Id });
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = bob.Id, ClientId = spa.Id, RealmId = realm.Id });

        // Role grants (realm + client)
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = alice.Id, RoleId = roleAdmin.Id, RealmId = realm.Id });
        db.UserClientRoleAssignments.Add(new UserClientRoleAssignment { UserId = alice.Id, RoleId = roleUser.Id, ClientId = spa.Id });

        // Consent for alice on spa: profile + email + roles
        db.Consents.Add(new Consent
        {
            UserId = alice.Id,
            ClientId = spa.ClientId,
            ScopesJson = JsonSerializer.Serialize(new[] { "profile", "email", "roles" })
        });

        await db.SaveChangesAsync();

        return new SeedData
        {
            Realm = realm,
            Users = new() { ["alice"] = alice, ["bob"] = bob },
            Clients = new() { ["spa"] = spa, ["conf"] = conf },
            Roles = new() { ["admin"] = roleAdmin, ["user"] = roleUser },
            Scopes = scopes.ToDictionary(s => s.Name)
        };
    }

    /// <summary>
    /// Seed two realms with separate role catalogs and clients to test realm-scoped behavior.
    /// </summary>
    public static async Task<MultiRealmSeed> SeedMultiRealmAsync(AuthDbContext db)
    {
        var r1 = new Realm { Name = "r1", DisplayName = "Realm1" };
        var r2 = new Realm { Name = "r2", DisplayName = "Realm2" };
        db.Realms.AddRange(r1, r2);

        var scopes = new[] { new Scope { Name = "openid" }, new Scope { Name = "roles" } };
        foreach (var s in scopes) db.Scopes.Add(s);

        var r1Admin = new Role { Name = "admin", RealmId = r1.Id };
        var r2User = new Role { Name = "user", RealmId = r2.Id };
        db.Roles.AddRange(r1Admin, r2User);

        var u = new User { Username = "multi", Email = "multi@example.com", Name = "Multi" };
        db.Users.Add(u);

        var c1 = new ClientEntity { ClientId = "spa-r1", RealmId = r1.Id, RequirePkce = true, RequireConsent = false, AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app1/cb" }) };
        var c2 = new ClientEntity { ClientId = "spa-r2", RealmId = r2.Id, RequirePkce = true, RequireConsent = false, AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app2/cb" }) };
        db.Clients.AddRange(c1, c2);
        foreach (var s in scopes) { db.ClientScopes.Add(new ClientScope { ClientId = c1.Id, ScopeName = s.Name }); db.ClientScopes.Add(new ClientScope { ClientId = c2.Id, ScopeName = s.Name }); }

        // Assignments
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = u.Id, ClientId = c1.Id, RealmId = r1.Id });
        db.UserClientAssignments.Add(new UserClientAssignment { UserId = u.Id, ClientId = c2.Id, RealmId = r2.Id });

        // Roles: realm admin in r1 only; client role user in c2
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = u.Id, RoleId = r1Admin.Id, RealmId = r1.Id });
        db.UserClientRoleAssignments.Add(new UserClientRoleAssignment { UserId = u.Id, RoleId = r2User.Id, ClientId = c2.Id });

        await db.SaveChangesAsync();

        return new MultiRealmSeed
        {
            User = u,
            Realm1 = r1,
            Realm2 = r2,
            ClientR1 = c1,
            ClientR2 = c2,
            R1Admin = r1Admin,
            R2User = r2User
        };
    }

    public sealed class SeedData
    {
        public required Realm Realm { get; init; }
        public required Dictionary<string, User> Users { get; init; }
        public required Dictionary<string, ClientEntity> Clients { get; init; }
        public required Dictionary<string, Role> Roles { get; init; }
        public required Dictionary<string, Scope> Scopes { get; init; }
    }

    public sealed class MultiRealmSeed
    {
        public required User User { get; init; }
        public required Realm Realm1 { get; init; }
        public required Realm Realm2 { get; init; }
        public required ClientEntity ClientR1 { get; init; }
        public required ClientEntity ClientR2 { get; init; }
        public required Role R1Admin { get; init; }
        public required Role R2User { get; init; }
    }
}
