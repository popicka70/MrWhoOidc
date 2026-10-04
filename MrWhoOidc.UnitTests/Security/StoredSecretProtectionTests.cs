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

    // C13 (2026-10-04 assessment): private JWKs were written to the distributed (Redis) cache tier.
    [TestMethod]
    public async Task PrivateKeys_AreNeverWrittenToDistributedCache()
    {
        using var fixture = CreateFixture();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        await using var db = CreateDb(fixture.SecretProtector, tenantAccessor);
        var cache = new OptionsRecordingHybridCache();
        var keyStore = new KeyStore(db, tenantAccessor, cache, Options.Create(new KeyRotationOptions()), NullLogger<KeyStore>.Instance, fixture.SecretProtector);

        await keyStore.GetActiveSigningKeyAsync();
        await keyStore.GetActiveEncryptionKeyAsync();

        foreach (var key in new[] { "signing:key:active:", "enc:key:active:" })
        {
            var entry = cache.Options.Single(o => o.Key.StartsWith(key, StringComparison.Ordinal));
            Assert.IsTrue(entry.Value?.Flags?.HasFlag(Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryFlags.DisableDistributedCache) == true,
                $"{entry.Key} must not be cached in the distributed tier");
        }
    }

    private sealed class OptionsRecordingHybridCache : Microsoft.Extensions.Caching.Hybrid.HybridCache
    {
        public Dictionary<string, Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions?> Options { get; } = new();

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
        {
            Options[key] = options;
            return await factory(state, cancellationToken);
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override ValueTask SetAsync<T>(string key, T value, Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
