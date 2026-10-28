using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Settings;
using System.Security.Cryptography;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres")]
public sealed class KeyRotationServiceTests
{
    private static string CreatePrivateRsaJwkJson(int keySizeBits, string alg = "RS256")
    {
        var kid = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(keySizeBits);
        var jwk = RsaJwk.FromRSA(rsa, kid, alg: alg, includePrivate: true, use: "sig");
        return jwk.ToJson(includePrivate: true);
    }

    /// <summary>The tenant settings of a tenant without token lifetime overrides (the resolver's defaults).</summary>
    private static ITenantSettingsService SettingsService()
    {
        var mock = new Mock<ITenantSettingsService>();
        mock.Setup(x => x.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());
        return mock.Object;
    }

    [TestMethod]
    public async Task EnsureInitializedAsync_Rotates_And_Retires()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var testTenantId = Guid.NewGuid();

        // Seed a first key created sufficiently in the past to trigger rotation
        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Alg = "RS256",
            JwkJson = "{ }",
            CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(20),
            TenantId = testTenantId
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new KeyRotationOptions
        {
            Enabled = true,
            RsaKeySizeBits = 3072,
            RotationInterval = TimeSpan.FromDays(7),
            Overlap = TimeSpan.FromDays(2)
        });

        var mockKeyStore = new Mock<IKeyStore>();
        mockKeyStore.Setup(x => x.InvalidateActiveSigningKeyCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockKeyStore.Setup(x => x.InvalidatePublicJwksCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockTenantAccessor = new Mock<ITenantAccessor>();
        mockTenantAccessor.Setup(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = testTenantId,
            Slug = "test-tenant",
            Name = "Test Tenant",
            IssuerUri = "https://test.example.com"
        });

        var svc = new KeyRotationService(
            db,
            options,
            Options.Create(new AuthOptions()),
            SettingsService(),
            new TokenLifetimeResolver(),
            mockKeyStore.Object,
            mockTenantAccessor.Object,
            NullLogger<KeyRotationService>.Instance);
        await svc.EnsureInitializedAsync();

        // After rotation, there should be two keys (one old, one new)
        Assert.AreEqual(2, db.SigningKeys.Count());
        var rotatedKey = db.SigningKeys.OrderByDescending(k => k.CreatedAt).First();
        var rotatedJwk = new JsonWebKey(rotatedKey.JwkJson);
        Assert.AreEqual(384, Base64UrlEncoder.DecodeBytes(rotatedJwk.N).Length);

        // Move time forward: mark keys older than interval+overlap as retired by calling EnsureInitializedAsync again after updating CreatedAt
        var veryOld = db.SigningKeys.OrderBy(k => k.CreatedAt).First();
        veryOld.CreatedAt = DateTimeOffset.UtcNow - (options.Value.RotationInterval + options.Value.Overlap + TimeSpan.FromDays(1));
        await db.SaveChangesAsync();

        await svc.EnsureInitializedAsync();
        Assert.IsTrue(db.SigningKeys.Any(k => k.RetiredAt != null));

        // Verify cache invalidation was called
        mockKeyStore.Verify(x => x.InvalidateActiveSigningKeyCacheAsync(testTenantId, It.IsAny<CancellationToken>()), Times.Once);
        mockKeyStore.Verify(x => x.InvalidatePublicJwksCacheAsync(testTenantId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task EnsureInitializedAsync_Rotates_RsaSigningKey_WhenConfiguredSizeIncreases()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var testTenantId = Guid.NewGuid();

        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Alg = "RS256",
            JwkJson = CreatePrivateRsaJwkJson(2048),
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = testTenantId
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new KeyRotationOptions
        {
            Enabled = true,
            RsaKeySizeBits = 3072,
            RotationInterval = TimeSpan.FromDays(7),
            Overlap = TimeSpan.FromDays(2)
        });

        var mockKeyStore = new Mock<IKeyStore>();
        mockKeyStore.Setup(x => x.InvalidateActiveSigningKeyCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockKeyStore.Setup(x => x.InvalidatePublicJwksCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockTenantAccessor = new Mock<ITenantAccessor>();
        mockTenantAccessor.Setup(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = testTenantId,
            Slug = "test-tenant",
            Name = "Test Tenant",
            IssuerUri = "https://test.example.com"
        });

        var svc = new KeyRotationService(
            db,
            options,
            Options.Create(new AuthOptions()),
            SettingsService(),
            new TokenLifetimeResolver(),
            mockKeyStore.Object,
            mockTenantAccessor.Object,
            NullLogger<KeyRotationService>.Instance);
        await svc.EnsureInitializedAsync();

        Assert.AreEqual(2, db.SigningKeys.Count());
        var rotatedKey = db.SigningKeys.OrderByDescending(k => k.CreatedAt).First();
        var rotatedJwk = new JsonWebKey(rotatedKey.JwkJson);
        Assert.AreEqual(384, Base64UrlEncoder.DecodeBytes(rotatedJwk.N).Length);
        Assert.IsTrue(db.SigningKeys.All(k => k.RetiredAt == null));

        mockKeyStore.Verify(x => x.InvalidateActiveSigningKeyCacheAsync(testTenantId, It.IsAny<CancellationToken>()), Times.Once);
        mockKeyStore.Verify(x => x.InvalidatePublicJwksCacheAsync(testTenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureInitializedAsync_Rotates_TheEncryptionKey()
    {
        // The encryption key was never rotated, so one key encrypted every response of the tenant for its whole
        // lifetime.
        using var db = TestDataSeeder.CreateInMemoryDb();
        var testTenantId = Guid.NewGuid();

        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Use = "enc",
            Alg = "RS256",
            JwkJson = CreatePrivateRsaJwkJson(2048),
            CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(20),
            TenantId = testTenantId
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new KeyRotationOptions
        {
            Enabled = true,
            RsaKeySizeBits = 3072,
            RotationInterval = TimeSpan.FromDays(7),
            Overlap = TimeSpan.FromDays(2)
        });

        var mockKeyStore = new Mock<IKeyStore>();
        mockKeyStore.Setup(x => x.InvalidateActiveSigningKeyCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockKeyStore.Setup(x => x.InvalidateActiveEncryptionKeyCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockKeyStore.Setup(x => x.InvalidatePublicJwksCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockTenantAccessor = new Mock<ITenantAccessor>();
        mockTenantAccessor.Setup(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = testTenantId,
            Slug = "test-tenant",
            Name = "Test Tenant",
            IssuerUri = "https://test.example.com"
        });

        var svc = new KeyRotationService(
            db,
            options,
            Options.Create(new AuthOptions()),
            SettingsService(),
            new TokenLifetimeResolver(),
            mockKeyStore.Object,
            mockTenantAccessor.Object,
            NullLogger<KeyRotationService>.Instance);

        await svc.EnsureInitializedAsync();

        var encryptionKeys = db.SigningKeys.Where(k => k.Use == "enc").ToList();
        Assert.AreEqual(2, encryptionKeys.Count, "the aged-out encryption key must be replaced by a new one");
        Assert.IsTrue(encryptionKeys.Any(k => k.RetiredAt == null), "the new encryption key is in use");
        mockKeyStore.Verify(x => x.InvalidateActiveEncryptionKeyCacheAsync(testTenantId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task EnsureInitializedAsync_KeepsAKey_WhileATokenSignedWithItCanStillBeValid()
    {
        // The overlap must cover the token lifetime: a token signed with the key is still accepted while the key
        // is in the JWKS.
        using var db = TestDataSeeder.CreateInMemoryDb();
        var testTenantId = Guid.NewGuid();

        // Older than the rotation interval (so it is rotated) but younger than interval + token lifetime.
        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Alg = "RS256",
            JwkJson = "{ }",
            CreatedAt = DateTimeOffset.UtcNow - (TimeSpan.FromDays(7) + TimeSpan.FromMinutes(30)),
            TenantId = testTenantId
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new KeyRotationOptions
        {
            Enabled = true,
            RsaKeySizeBits = 3072,
            RotationInterval = TimeSpan.FromDays(7),
            // Shorter than the access token lifetime (one hour by default), so the lifetime is what counts.
            Overlap = TimeSpan.FromMinutes(1)
        });

        var mockKeyStore = new Mock<IKeyStore>();
        mockKeyStore.Setup(x => x.InvalidateActiveSigningKeyCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockKeyStore.Setup(x => x.InvalidatePublicJwksCacheAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockTenantAccessor = new Mock<ITenantAccessor>();
        mockTenantAccessor.Setup(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = testTenantId,
            Slug = "test-tenant",
            Name = "Test Tenant",
            IssuerUri = "https://test.example.com"
        });

        var svc = new KeyRotationService(
            db,
            options,
            Options.Create(new AuthOptions { TokenValidationClockSkewSeconds = 60 }),
            SettingsService(),
            new TokenLifetimeResolver(),
            mockKeyStore.Object,
            mockTenantAccessor.Object,
            NullLogger<KeyRotationService>.Instance);

        await svc.EnsureInitializedAsync();

        var old = db.SigningKeys.OrderBy(k => k.CreatedAt).First();
        Assert.IsNull(old.RetiredAt, "the key must stay in the JWKS while a token signed with it can still be valid");
    }
}
