using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// M-Import: the configuration import could hand out the protected scopes (<c>tenants</c> and
/// <c>mrwho:admin</c>) to a tenant admin's client, and it did not validate the imported identity
/// providers with the validator the admin UI and API use.
/// </summary>
[TestClass]
public sealed class ImportProtectedScopePolicyTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static (AuthDbContext db, ConfigurationImportService service) CreateService()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new AuthDbContext(options);

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("hash");

        var service = new ConfigurationImportService(
            db, hasher.Object, NullLogger<ConfigurationImportService>.Instance, Mock.Of<IUserAccountService>());

        return (db, service);
    }

    private static async Task SeedAsync(AuthDbContext db)
    {
        db.Tenants.Add(new Tenant { Id = TenantId, Slug = "acme", Name = "Acme", Status = TenantStatus.Active });
        db.Realms.Add(new Realm { TenantId = TenantId, Name = "admin", DisplayName = "Admin" });
        await db.SaveChangesAsync();
    }

    private static ExportManifest ClientManifest(params string[] scopes) => new()
    {
        Version = 1,
        ExportType = "client",
        Data = new SeedManifest
        {
            Version = 1,
            Clients =
            [
                new ClientSeedDefinition
                {
                    ClientId = "imported-client",
                    ClientName = "Imported Client",
                    Realm = "admin",
                    AllowedScopes = [.. scopes]
                }
            ]
        }
    };

    [TestMethod]
    public async Task TenantAdminImport_DoesNotAssignTheProtectedScopes()
    {
        var (db, service) = CreateService();
        await SeedAsync(db);

        var result = await service.ImportClientAsync(
            ClientManifest("openid", "profile", "tenants", "mrwho:admin"),
            new ImportOptions { TargetTenantId = TenantId, IsPlatformAdmin = false });

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));

        var client = await db.Clients.IgnoreQueryFilters().SingleAsync(c => c.ClientId == "imported-client");
        var assigned = await db.ClientScopes.IgnoreQueryFilters()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync();

        CollectionAssert.Contains(assigned, "openid");
        CollectionAssert.Contains(assigned, "profile");
        CollectionAssert.DoesNotContain(assigned, "tenants",
            "the tenants claim lists the user's memberships in EVERY tenant: a tenant admin must not hand it out");
        CollectionAssert.DoesNotContain(assigned, "mrwho:admin",
            "the admin API scope is a platform capability");
    }

    [TestMethod]
    public async Task PlatformAdminImport_AssignsTheTenantsScope_ButNotTheAdminApiScope()
    {
        var (db, service) = CreateService();
        await SeedAsync(db);

        var result = await service.ImportClientAsync(
            ClientManifest("openid", "tenants", "mrwho:admin"),
            new ImportOptions { TargetTenantId = TenantId, IsPlatformAdmin = true });

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));

        var client = await db.Clients.IgnoreQueryFilters().SingleAsync(c => c.ClientId == "imported-client");
        var assigned = await db.ClientScopes.IgnoreQueryFilters()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync();

        CollectionAssert.Contains(assigned, "tenants");
        CollectionAssert.DoesNotContain(assigned, "mrwho:admin",
            "an imported client is never a designated admin client, so it could not obtain an admin API token");
    }

    [TestMethod]
    public async Task ImportedProvider_IsValidated()
    {
        var (db, service) = CreateService();
        await SeedAsync(db);

        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "provider",
            Data = new SeedManifest
            {
                Version = 1,
                IdentityProviders =
                [
                    new IdentityProviderSeedDefinition
                    {
                        // An empty name is rejected by the provider validator.
                        Name = string.Empty,
                        DisplayName = "Broken provider"
                    }
                ]
            }
        };

        var result = await service.ImportIdentityProviderAsync(
            manifest, new ImportOptions { TargetTenantId = TenantId, IsPlatformAdmin = true });

        Assert.IsFalse(result.Success, "an invalid provider must not be imported");
        Assert.IsTrue(result.Errors.Any(e => e.Code == "PROVIDER_INVALID"),
            "the import must report the validation failure");
        Assert.AreEqual(0, await db.IdentityProviders.IgnoreQueryFilters().CountAsync());
    }
}
