using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Seeding;

public static class DatabaseSeeder
{
    public static async Task EnsureSeedDataAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<ISeeder>();
        await seeder.SeedAsync(ct).ConfigureAwait(false);

        // Ensure at least one signing key exists
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
        await keyStore.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);

        // Apply rotation policies
        var rotation = scope.ServiceProvider.GetRequiredService<IKeyRotationService>();
        await rotation.EnsureInitializedAsync(ct).ConfigureAwait(false);
    }
}
