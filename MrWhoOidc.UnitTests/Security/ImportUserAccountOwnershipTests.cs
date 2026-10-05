using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Tenant import matched the global account by USERNAME alone and overwrote its password, then added the imported
/// tenant to it (the K1 pattern). Password writes also bypassed UpdatePasswordAsync (C14: no stamp rotation, no
/// token revocation).
/// </summary>
[TestClass]
public sealed class ImportUserAccountOwnershipTests
{
    private static ExportManifest Manifest(string username, string? email = null) => new()
    {
        ExportType = "tenant",
        Data = new SeedManifest
        {
            Tenants =
            [
                new TenantSeedDefinition
                {
                    Slug = "imported",
                    Name = "Imported",
                    Users = [new UserSeedDefinition { Username = username, Email = email, Password = "Imported-Pa55word!" }]
                }
            ]
        }
    };

    private static ConfigurationImportService CreateService(AuthDbContext db, IUserAccountService accounts)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("imported-hash");
        return new ConfigurationImportService(db, hasher.Object, NullLogger<ConfigurationImportService>.Instance, accounts);
    }

    [TestMethod]
    [DataRow("victim", null, DisplayName = "same username")]
    [DataRow("someone-else", "victim@example.com", DisplayName = "same email")]
    public async Task Import_NeverWritesAForeignAccount(string username, string? email)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = new UserAccount { Username = "victim", Email = "victim@example.com", NormalizedEmail = EmailNormalizer.NormalizeForLookup("victim@example.com"), PasswordHash = "victim-hash", SecurityStamp = "s" };
        db.UserAccounts.Add(victim);
        await db.SaveChangesAsync();

        var result = await CreateService(db, new UserAccountService(db)).ImportTenantAsync(Manifest(username, email), new ImportOptions());

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        var reloaded = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == victim.Id);
        Assert.AreEqual("victim-hash", reloaded.PasswordHash);
        Assert.IsFalse(await db.UserTenantMemberships.AnyAsync(m => m.UserAccountId == victim.Id), "the import must not add its tenant to a foreign account");
    }

    [TestMethod]
    public async Task Import_NewUser_SetsPasswordThroughAccountService()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var accounts = new Mock<IUserAccountService>();

        var result = await CreateService(db, accounts.Object).ImportTenantAsync(Manifest("fresh"), new ImportOptions());

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Username == "fresh");
        Assert.AreEqual(user.Id, user.UserAccountId);
        accounts.Verify(a => a.UpdatePasswordAsync(user.Id, "imported-hash", null, "argon2id", It.IsAny<CancellationToken>()), Times.Once);
    }
}
