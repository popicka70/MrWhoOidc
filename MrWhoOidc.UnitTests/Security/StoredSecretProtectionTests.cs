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

    // Third 2026-10-04 review: IdentityProviderKey.Jwk (the private keys used to sign upstream request objects) was
    // stored in plaintext.
    [TestMethod]
    public async Task ProviderPrivateJwk_IsProtectedAtRest_AndExportedInFullModeAsPlainJwk()
    {
        using var fixture = CreateFixture();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        await using var db = CreateDb(fixture.SecretProtector, tenantAccessor);
        var tenantId = tenantAccessor.CurrentTenant!.TenantId;
        db.Tenants.Add(new Tenant { Id = tenantId, Slug = "default", Name = "Default", IssuerUri = "https://idp/t/default" });
        var provider = new IdentityProvider { Name = "entra", TenantId = tenantId };
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var privateJwk = System.Text.Json.JsonSerializer.Serialize(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ec)));
        db.IdentityProviders.Add(provider);
        db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Alg = "ES256", Kid = "k1", Jwk = privateJwk });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.IdentityProviderKeys.SingleAsync();
        Assert.IsTrue(stored.Jwk.StartsWith("dp:v1:", StringComparison.Ordinal), "the private key must not be stored in plaintext");
        Assert.IsFalse(stored.Jwk.Contains("\"d\"", StringComparison.Ordinal));
        db.ChangeTracker.Clear();

        var manifest = await new MrWhoOidc.WebAuth.Services.ConfigurationExportService(db, NullLogger<MrWhoOidc.WebAuth.Services.ConfigurationExportService>.Instance, fixture.SecretProtector)
            .ExportIdentityProviderAsync(provider.Id, new MrWhoOidc.Auth.Seeding.ExportOptions { Mode = MrWhoOidc.Auth.Seeding.ExportMode.Full });
        Assert.AreEqual(privateJwk, manifest.Data.IdentityProviders!.Single().Keys.Single().Jwk, "readers get the JWK back");
    }

    [TestMethod]
    public async Task ProviderKeyBackfill_ProtectsLegacyPlaintextRows_Once()
    {
        using var fixture = CreateFixture();
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        await using (var legacy = new AuthDbContext(options, null, null))
        {
            legacy.IdentityProviderKeys.AddRange(
                new IdentityProviderKey { IdentityProviderId = Guid.NewGuid(), Jwk = "{\"kty\":\"EC\",\"d\":\"secret\"}" },
                new IdentityProviderKey { IdentityProviderId = Guid.NewGuid(), Jwk = string.Empty });
            await legacy.SaveChangesAsync();
        }

        await using var db = new AuthDbContext(options, null, fixture.SecretProtector);
        Assert.AreEqual(1, await ProviderKeyProtectionBackfill.RunAsync(db, fixture.SecretProtector, NullLogger.Instance));
        Assert.AreEqual(0, await ProviderKeyProtectionBackfill.RunAsync(db, fixture.SecretProtector, NullLogger.Instance), "idempotent");

        db.ChangeTracker.Clear();
        var keys = await db.IdentityProviderKeys.ToListAsync();
        Assert.IsTrue(keys.Single(k => k.Jwk != string.Empty).Jwk.StartsWith("dp:v1:", StringComparison.Ordinal));
        Assert.AreEqual("{\"kty\":\"EC\",\"d\":\"secret\"}", fixture.SecretProtector.UnprotectProviderKeyJwk(keys.Single(k => k.Jwk != string.Empty).Jwk));
    }

    // Wave 2: SigningKey.JwkJson and TOTP secrets were only protected lazily on read, with no backfill.
    [TestMethod]
    public async Task StoredSecretBackfill_ProtectsLegacySigningKeysAndTotpSecrets_Once()
    {
        using var fixture = CreateFixture();
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var tenantId = Guid.NewGuid();
        await using (var legacy = new AuthDbContext(options, null, null))
        {
            legacy.SigningKeys.Add(new SigningKey { Kid = "legacy", Alg = "RS256", JwkJson = "{\"kty\":\"RSA\",\"d\":\"secret\"}" });
            legacy.UserAccounts.Add(new UserAccount { Username = "a", Email = "a@example.test", NormalizedEmail = "a@example.test", TotpSecret = "JBSWY3DPEHPK3PXP" });
            legacy.Users.Add(new User { TenantId = tenantId, Username = "u", TotpSecret = "KRSXG5CTMVRXEZLU" });
            legacy.UserAccounts.Add(new UserAccount { Username = "b", Email = "b@example.test", NormalizedEmail = "b@example.test" });
            await legacy.SaveChangesAsync();
        }

        await using var db = new AuthDbContext(options, null, null);
        Assert.AreEqual(3, await StoredSecretProtectionBackfill.RunAsync(db, fixture.SecretProtector, NullLogger.Instance));
        Assert.AreEqual(0, await StoredSecretProtectionBackfill.RunAsync(db, fixture.SecretProtector, NullLogger.Instance), "idempotent");

        db.ChangeTracker.Clear();
        var key = await db.SigningKeys.IgnoreQueryFilters().SingleAsync();
        var account = await db.UserAccounts.SingleAsync(a => a.Username == "a");
        var user = await db.Users.IgnoreQueryFilters().SingleAsync();
        Assert.IsTrue(key.JwkJson.StartsWith("dp:v1:", StringComparison.Ordinal));
        Assert.IsTrue(account.TotpSecret!.StartsWith("dp:v1:", StringComparison.Ordinal));
        Assert.IsTrue(user.TotpSecret!.StartsWith("dp:v1:", StringComparison.Ordinal));
        Assert.AreEqual("{\"kty\":\"RSA\",\"d\":\"secret\"}", fixture.SecretProtector.UnprotectSigningKeyJwk(key.JwkJson));
        Assert.AreEqual("JBSWY3DPEHPK3PXP", fixture.SecretProtector.UnprotectTotpSecret(account.TotpSecret));
        Assert.AreEqual("KRSXG5CTMVRXEZLU", fixture.SecretProtector.UnprotectTotpSecret(user.TotpSecret));
        Assert.IsNull((await db.UserAccounts.SingleAsync(a => a.Username == "b")).TotpSecret);
    }

    // After the backfill a plaintext value can only have been planted around the application (e.g. a known TOTP
    // secret written straight into the database). It must not be used - and must not read as "no MFA" either.
    [TestMethod]
    public void RejectPlaintextSecrets_AfterBackfill_RefusesPlaintextSigningKeysAndTotpSecrets()
    {
        using var fixture = CreateFixture();
        var policy = new PlaintextSecretPolicy(Options.Create(new SecretProtectionOptions()));
        var protector = fixture.Create(policy);
        var protectedTotp = protector.ProtectTotpSecret("JBSWY3DPEHPK3PXP");

        Assert.AreEqual("JBSWY3DPEHPK3PXP", protector.UnprotectTotpSecret("JBSWY3DPEHPK3PXP"), "before the backfill legacy rows still read");

        policy.MarkBackfillCompleted();

        Assert.ThrowsExactly<PlaintextSecretRejectedException>(() => protector.UnprotectTotpSecret("JBSWY3DPEHPK3PXP"));
        Assert.ThrowsExactly<PlaintextSecretRejectedException>(() => protector.UnprotectSigningKeyJwk("{\"kty\":\"RSA\"}"));
        Assert.AreEqual("JBSWY3DPEHPK3PXP", protector.UnprotectTotpSecret(protectedTotp), "protected values still read");
        Assert.IsNull(protector.UnprotectTotpSecret(null), "no secret is not a plaintext secret");
    }

    [TestMethod]
    public void RejectPlaintextSecrets_DefaultsOn_AndCanBeDisabled()
    {
        Assert.IsTrue(new SecretProtectionOptions().RejectPlaintextSecrets);

        using var fixture = CreateFixture();
        var policy = new PlaintextSecretPolicy(Options.Create(new SecretProtectionOptions { RejectPlaintextSecrets = false }));
        policy.MarkBackfillCompleted();

        Assert.AreEqual("JBSWY3DPEHPK3PXP", fixture.Create(policy).UnprotectTotpSecret("JBSWY3DPEHPK3PXP"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{\"kty\":\"EC\"}")]
    public void UnprotectProviderKeyJwk_PassesLegacyAndEmptyValuesThrough(string stored)
    {
        using var fixture = CreateFixture();
        Assert.AreEqual(stored, fixture.SecretProtector.UnprotectProviderKeyJwk(stored));
        Assert.AreEqual(stored, ((ISecretProtector?)null).UnprotectProviderKeyJwk(stored));
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
            _provider = DataProtectionProvider.Create(new DirectoryInfo(_directory));
            SecretProtector = new DataProtectionSecretProtector(_provider);
        }

        private readonly IDataProtectionProvider _provider;

        public ISecretProtector SecretProtector { get; }

        public ISecretProtector Create(PlaintextSecretPolicy policy) => new DataProtectionSecretProtector(_provider, policy);

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
