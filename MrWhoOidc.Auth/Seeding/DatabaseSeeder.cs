using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Seeding;

public static class DatabaseSeeder
{
    public static async Task EnsureSeedDataAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Services.IPasswordHasher>();

        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.Add(new User
            {
                Username = "alice",
                PasswordHash = hasher.Hash("P@ssw0rd!"),
                HashAlgorithm = "argon2id"
            });
        }

        if (!await db.Clients.AnyAsync(ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "test-client",
                ClientName = "Test Client",
                RequireConsent = false,
                RequirePkce = true
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
