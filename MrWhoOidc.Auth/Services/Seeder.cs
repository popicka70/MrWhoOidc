using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface ISeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public sealed class Seeder(AuthDbContext db, IPasswordHasher hasher) : ISeeder
{
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

        if (!await db.Users.AnyAsync(ct).ConfigureAwait(false))
        {
            db.Users.Add(new User
            {
                Username = "alice",
                PasswordHash = hasher.Hash("P@ssw0rd!"),
                HashAlgorithm = "argon2id",
                Name = "Alice Adams",
                Email = "alice@example.com",
                EmailVerified = true
            });
        }

        if (!await db.Clients.AnyAsync(c => c.ClientId == "blazor-web", ct).ConfigureAwait(false))
        {
            db.Clients.Add(new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = null,
                RealmId = adminRealm.Id
            });
        }

        // backfill RealmId for any existing client rows missing it
        var clientsWithoutRealm = await db.Clients.Where(c => c.RealmId == null).ToListAsync(ct).ConfigureAwait(false);
        foreach (var c in clientsWithoutRealm)
        {
            c.RealmId = adminRealm.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
