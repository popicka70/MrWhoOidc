using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests;

namespace MrWhoOidc.UnitTests.Infrastructure;

[TestClass]
public class HealthCheckPerformanceTests
{
    private AuthDbContext _db = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = TestDataSeeder.CreateInMemoryDb();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    private async Task SeedDataAsync(int clientCount)
    {
        var realm = new Realm { Name = "default" };
        _db.Realms.Add(realm);
        await _db.SaveChangesAsync();

        var clients = new List<ClientEntity>();
        var secrets = new List<ClientSecret>();

        for (int i = 0; i < clientCount; i++)
        {
            var client = new ClientEntity
            {
                ClientId = $"client-{i}",
                TenantId = Guid.NewGuid(),
                RealmId = realm.Id
            };
            clients.Add(client);

            bool isCritical = i % 2 == 0;

            if (isCritical)
            {
                secrets.Add(new ClientSecret
                {
                    ClientId = client.Id,
                    ActivatedAtUtc = DateTime.UtcNow.AddDays(-10),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(-1),
                    RevokedAtUtc = null,
                    SecretHash = "hash"
                });
            }
            else
            {
                secrets.Add(new ClientSecret
                {
                    ClientId = client.Id,
                    ActivatedAtUtc = DateTime.UtcNow.AddDays(-10),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(10),
                    RevokedAtUtc = null,
                    SecretHash = "hash"
                });
            }
        }

        _db.Clients.AddRange(clients);
        _db.ClientSecrets.AddRange(secrets);
        await _db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task HealthCheck_FunctionalVerification()
    {
        int clientCount = 10;
        await SeedDataAsync(clientCount);

        var now = DateTime.UtcNow;
        var ct = default(System.Threading.CancellationToken);

        var criticalClients = await _db.Clients
            .AsNoTracking()
            .Where(c => c.ClientSecrets.Any() && !c.ClientSecrets.Any(s =>
                s.ActivatedAtUtc != null
                && s.RevokedAtUtc == null
                && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now)))
            .Select(c => new { clientId = c.ClientId, tenantId = c.TenantId })
            .ToListAsync(ct);

        Assert.AreEqual(clientCount / 2, criticalClients.Count);
        Console.WriteLine($"Found {criticalClients.Count} critical clients as expected.");
    }
}
