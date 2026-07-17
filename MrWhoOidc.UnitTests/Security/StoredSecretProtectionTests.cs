using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class StoredSecretProtectionTests
{
    [TestMethod]
    public async Task SigningPrivateJwk_IsProtectedAtRest_AndReadableThroughKeyStore()
    {
        using var fixture = CreateFixture();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        await using var db = CreateDb(fixture.SecretProtector, tenantAccessor);
        var keyStore = new KeyStore(
            db,
            tenantAccessor,
            new TestHybridCache(),
            Options.Create(new KeyRotationOptions()),
            NullLogger<KeyStore>.Instance,
            fixture.SecretProtector);

        var activeKey = await keyStore.GetActiveSigningKeyAsync();
        db.ChangeTracker.Clear();

        var stored = await db.SigningKeys.IgnoreQueryFilters().SingleAsync();

        Assert.IsTrue(stored.JwkJson.StartsWith("dp:v1:", StringComparison.Ordinal));
        var unprotectedStoredKey = new JsonWebKey(fixture.SecretProtector.UnprotectSigningKeyJwk(stored.JwkJson));
        Assert.AreEqual(activeKey.Kid, unprotectedStoredKey.Kid);

        var reloadedKey = await keyStore.GetActiveSigningKeyAsync();
        Assert.AreEqual(activeKey.Kid, reloadedKey.Kid);
    }

    [TestMethod]
    public async Task TotpSecret_IsProtectedAtRest_AndReturnedPlaintextThroughService()
    {
        using var fixture = CreateFixture();
        await using var db = CreateDb(fixture.SecretProtector, MockTenantAccessor.CreateSingleTenantMode());
        var userAccountService = new UserAccountService(db, secretProtector: fixture.SecretProtector);
        var account = new UserAccount
        {
            Username = "mfa-user",
            Email = "mfa@example.test",
            NormalizedEmail = "mfa@example.test",
            PasswordHash = "hash",
            HashAlgorithm = "argon2id"
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        await userAccountService.EnableMfaAsync(account.Id, "JBSWY3DPEHPK3PXP");
        db.ChangeTracker.Clear();

        var stored = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.IsTrue(stored.TotpSecret!.StartsWith("dp:v1:", StringComparison.Ordinal));

        var status = await userAccountService.GetMfaStatusAsync(account.Id);
        Assert.AreEqual("JBSWY3DPEHPK3PXP", status.Secret);
    }

    private static AuthDbContext CreateDb(ISecretProtector secretProtector, MockTenantAccessor tenantAccessor)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(options, tenantAccessor, secretProtector);
    }

    private static SecretProtectionFixture CreateFixture() => new();

    private sealed class SecretProtectionFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mrwho-oidc-dp-" + Guid.NewGuid().ToString("N"));

        public SecretProtectionFixture()
        {
            Directory.CreateDirectory(_directory);
            var provider = DataProtectionProvider.Create(new DirectoryInfo(_directory));
            SecretProtector = new DataProtectionSecretProtector(provider);
        }

        public ISecretProtector SecretProtector { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
