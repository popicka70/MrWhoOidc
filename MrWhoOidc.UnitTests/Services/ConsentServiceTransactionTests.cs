using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class ConsentServiceTransactionTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task GrantConsentAsync_WorksWithExecutionStrategy()
    {
        // Arrange
        using var db = CreateDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var service = new ConsentService(db, tenantAccessor);
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid", "profile", "email" };

        // Act
        await service.GrantConsentAsync(userId, clientId, scopes).ConfigureAwait(false);

        // Assert
        var consent = await db.Consents.FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId);
        Assert.IsNotNull(consent);
        var grantedScopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(consent.ScopesJson);
        Assert.IsTrue(grantedScopes!.Contains("profile"));
        Assert.IsTrue(grantedScopes!.Contains("email"));
        Assert.IsFalse(grantedScopes!.Contains("openid")); // openid is filtered out
    }

    [TestMethod]
    public async Task GrantConsentAsync_UpdatesExistingConsent()
    {
        // Arrange
        using var db = CreateDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var service = new ConsentService(db, tenantAccessor);
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        
        db.Consents.Add(new Consent
        {
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[\"profile\"]",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenantAccessor.CurrentTenant!.TenantId
        });
        await db.SaveChangesAsync();

        // Act
        await service.GrantConsentAsync(userId, clientId, new[] { "email" }).ConfigureAwait(false);

        // Assert
        var consent = await db.Consents.FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId);
        Assert.IsNotNull(consent);
        var grantedScopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(consent.ScopesJson);
        Assert.AreEqual(2, grantedScopes!.Length);
        Assert.IsTrue(grantedScopes.Contains("profile"));
        Assert.IsTrue(grantedScopes.Contains("email"));
    }
}
