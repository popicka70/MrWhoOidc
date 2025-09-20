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

        // Ensure test-client exists
        if (!await db.Clients.AnyAsync(c => c.ClientId == "test-client", ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "test-client",
                ClientName = "Test Client",
                RequireConsent = false,
                RequirePkce = true
            });
        }

        // Ensure blazor-web client exists for the Blazor app OIDC login
        if (!await db.Clients.AnyAsync(c => c.ClientId == "blazor-web", ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
