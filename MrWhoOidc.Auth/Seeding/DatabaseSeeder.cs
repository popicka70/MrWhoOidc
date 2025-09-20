using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

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
                HashAlgorithm = "argon2id",
                Name = "Alice Adams",
                Email = "alice@example.com",
                EmailVerified = true
            });
        }

        // Public clients
        if (!await db.Clients.AnyAsync(c => c.ClientId == "test-client", ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "test-client",
                ClientName = "Test Client",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = null // public client
            });
        }

        if (!await db.Clients.AnyAsync(c => c.ClientId == "blazor-web", ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "blazor-web",
                ClientName = "Blazor Web Frontend",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = null // public client
            });
        }

        // Confidential client for token/revocation tests
        if (!await db.Clients.AnyAsync(c => c.ClientId == "test-confidential", ct))
        {
            db.Clients.Add(new Client
            {
                ClientId = "test-confidential",
                ClientName = "Test Confidential Client",
                RequireConsent = false,
                RequirePkce = true,
                ClientSecretHash = hasher.Hash("secret123!")
            });
        }

        await db.SaveChangesAsync(ct);

        // Initialize key rotation on first run (ensures overlap/retirement policy is applied)
        var rotation = scope.ServiceProvider.GetRequiredService<IKeyRotationService>();
        await rotation.EnsureInitializedAsync(ct);
    }
}
