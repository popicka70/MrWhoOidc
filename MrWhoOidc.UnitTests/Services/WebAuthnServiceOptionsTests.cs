using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public class WebAuthnServiceOptionsTests
{
    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantContext? CurrentTenant { get; private set; }

        public void SetTenant(TenantContext context)
        {
            CurrentTenant = context;
        }
    }

    [TestMethod]
    public async Task CreateAuthenticationChallengeAsync_Throws_WhenUsernamelessDisabled()
    {
        var tenantId = Guid.NewGuid();
        var tenantAccessor = new StubTenantAccessor();
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://issuer/default",
            IsMultiTenantMode = false
        });

        await using var db = CreateDbContext();
        var fido2 = new Mock<IFido2>(MockBehavior.Strict);
        var service = new WebAuthnService(
            db,
            fido2.Object,
            CreateHybridCache(),
            tenantAccessor,
            Options.Create(new WebAuthnOptions
            {
                Enabled = true,
                AllowUsernamelessAuthentication = false
            }),
            NullLogger<WebAuthnService>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.CreateAuthenticationChallengeAsync(username: null));

        fido2.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task CreateRegistrationChallengeAsync_Throws_WhenMaxCredentialsReached()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantAccessor = new StubTenantAccessor();
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://issuer/default",
            IsMultiTenantMode = false
        });

        await using var db = CreateDbContext();
        db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            CredentialId = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            PublicKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            Type = "public-key",
            SignatureCounter = 0,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            Username = "alice"
        };

        var fido2 = new Mock<IFido2>(MockBehavior.Strict);
        var service = new WebAuthnService(
            db,
            fido2.Object,
            CreateHybridCache(),
            tenantAccessor,
            Options.Create(new WebAuthnOptions
            {
                Enabled = true,
                MaxCredentialsPerUser = 1
            }),
            NullLogger<WebAuthnService>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.CreateRegistrationChallengeAsync(user));

        fido2.VerifyNoOtherCalls();
    }

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase("webauthn-options-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
