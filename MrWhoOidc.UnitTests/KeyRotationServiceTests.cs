using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres")]
public sealed class KeyRotationServiceTests
{
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
        
        var svc = new KeyRotationService(db, options, mockKeyStore.Object, mockTenantAccessor.Object, NullLogger<KeyRotationService>.Instance);
        await svc.EnsureInitializedAsync();

        // After rotation, there should be two keys (one old, one new)
        Assert.AreEqual(2, db.SigningKeys.Count());

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
}
