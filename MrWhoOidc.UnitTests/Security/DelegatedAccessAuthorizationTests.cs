using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class DelegatedAccessAuthorizationTests
{
    [TestMethod]
    public async Task Authorize_ProfileRead_AllowsDelegatorAccountResource()
    {
        await using var fixture = await CreateFixtureAsync();

        var context = await fixture.Service.AuthorizeAsync(
            fixture.Actor,
            fixture.Grant.Id,
            fixture.ClientId,
            "profile.read",
            new DelegatedResource("user", fixture.DelegatorId.ToString(), null));

        Assert.AreEqual(AccessContextKind.DelegatedAccess, context.Kind);
        Assert.AreEqual(fixture.DelegatorId, context.SubjectUserAccountId);
        Assert.AreEqual(fixture.DelegateId, context.ActorUserAccountId);

        var persistedGrant = await fixture.Db.DelegatedAccessGrants
            .AsNoTracking()
            .SingleAsync(grant => grant.Id == fixture.Grant.Id);
        Assert.AreEqual(1L, persistedGrant.UseCount);
        Assert.IsNotNull(persistedGrant.LastUsedAt);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("{}")]
    [DataRow("not-json")]
    public async Task Authorize_ProfileRead_DeniesMissingOrMalformedPolicy(string policyJson)
    {
        await using var fixture = await CreateFixtureAsync(policyJson);

        await Assert.ThrowsExactlyAsync<ResourceError>(() => fixture.Service.AuthorizeAsync(
            fixture.Actor,
            fixture.Grant.Id,
            fixture.ClientId,
            "profile.read",
            new DelegatedResource("user", fixture.DelegatorId.ToString(), null)));
    }

    [TestMethod]
    public async Task Authorize_ProfileRead_DeniesWrongResourceType()
    {
        await using var fixture = await CreateFixtureAsync();

        await Assert.ThrowsExactlyAsync<ResourceError>(() => fixture.Service.AuthorizeAsync(
            fixture.Actor,
            fixture.Grant.Id,
            fixture.ClientId,
            "profile.read",
            new DelegatedResource("profile", fixture.DelegatorId.ToString(), null)));
    }

    [TestMethod]
    public async Task Authorize_ProfileRead_DeniesDifferentAccountResource()
    {
        await using var fixture = await CreateFixtureAsync();

        await Assert.ThrowsExactlyAsync<ResourceError>(() => fixture.Service.AuthorizeAsync(
            fixture.Actor,
            fixture.Grant.Id,
            fixture.ClientId,
            "profile.read",
            new DelegatedResource("user", Guid.NewGuid().ToString(), null)));
    }

    [TestMethod]
    public async Task Authorize_ProfileRead_DeniesCallerSuppliedConstraintJson()
    {
        await using var fixture = await CreateFixtureAsync();

        await Assert.ThrowsExactlyAsync<ResourceError>(() => fixture.Service.AuthorizeAsync(
            fixture.Actor,
            fixture.Grant.Id,
            fixture.ClientId,
            "profile.read",
            new DelegatedResource("user", fixture.DelegatorId.ToString(), "{}")));
    }

    private static async Task<AuthorizationFixture> CreateFixtureAsync(string? policyJson = null)
    {
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var tenantId = Guid.NewGuid();
        var delegatorId = Guid.NewGuid();
        var delegateId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var grant = new DelegatedAccessGrant
        {
            TenantId = tenantId,
            ClientId = clientId,
            DelegatorUserAccountId = delegatorId,
            DelegateUserAccountId = delegateId,
            Status = DelegatedAccessGrantStatus.Active,
            CapabilitiesJson = "[\"profile.read\"]",
            ResourceConstraintsJson = policyJson ??
                $"{{\"profile.read\":{{\"allowedTypes\":[\"user\"],\"allowedIds\":[\"{delegatorId}\"]}}}}",
            Purpose = "Assist with profile review",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AcceptanceExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "delegation-test",
            Name = "Delegation Test",
            IssuerUri = "https://issuer.example/delegation-test",
            Status = TenantStatus.Active
        });
        db.UserAccounts.AddRange(
            new UserAccount { Id = delegatorId, Username = "delegator" },
            new UserAccount { Id = delegateId, Username = "delegate" });
        db.UserTenantMemberships.AddRange(
            new UserTenantMembership { UserAccountId = delegatorId, TenantId = tenantId },
            new UserTenantMembership { UserAccountId = delegateId, TenantId = tenantId });
        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "default" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientId,
            TenantId = tenantId,
            RealmId = realmId,
            ClientId = "delegated-client"
        });
        db.DelegatedAccessGrants.Add(grant);
        await db.SaveChangesAsync();

        var service = new DelegatedAccessAuthorizationService(
            db,
            new DelegableCapabilityCatalog(),
            new UserTenantMembershipService(db),
            new NoopAuditSink(),
            Options.Create(new AuthOptions { EnableDelegatedAccess = true }),
            NullLogger<DelegatedAccessAuthorizationService>.Instance);
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(UserClaimTypes.UserAccountId, delegateId.ToString())],
            "test"));

        return new AuthorizationFixture(db, service, actor, grant, clientId, delegatorId, delegateId);
    }

    private sealed record AuthorizationFixture(
        AuthDbContext Db,
        DelegatedAccessAuthorizationService Service,
        ClaimsPrincipal Actor,
        DelegatedAccessGrant Grant,
        Guid ClientId,
        Guid DelegatorId,
        Guid DelegateId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}