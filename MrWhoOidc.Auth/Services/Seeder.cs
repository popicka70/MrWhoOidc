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
    // Initial constant secret for the blazor-web client (development only)
    private const string InitialBlazorWebClientSecret = "blazor-web-initial-secret";

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

        // Ensure blazor-web client exists as a confidential client with an initial constant secret
        var blazorWebClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == "blazor-web", ct).ConfigureAwait(false);
        if (blazorWebClient is null)
        {
            db.Clients.Add(new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = hasher.Hash(InitialBlazorWebClientSecret),
                RealmId = adminRealm.Id
            });
        }
        else if (string.IsNullOrEmpty(blazorWebClient.ClientSecretHash))
        {
            // Backfill a secret if previously created as public client
            blazorWebClient.ClientSecretHash = hasher.Hash(InitialBlazorWebClientSecret);
            blazorWebClient.RequirePkce = true;
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
