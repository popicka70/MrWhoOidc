using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class DelegatedAccessGrantLifecycleTests
{
    [TestMethod]
    public async Task AcceptGrant_PersistsGrantAndConsumesInvitation()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var grant = await fixture.CreateGrantAsync();

        await fixture.Service.AcceptGrantAsync(fixture.InvitationToken, fixture.DelegateId);

        fixture.Db.ChangeTracker.Clear();
        var persistedGrant = await fixture.Db.DelegatedAccessGrants.SingleAsync(candidate => candidate.Id == grant.Id);
        var invitation = await fixture.Db.DelegatedAccessInvitationTokens.SingleAsync(candidate => candidate.GrantId == grant.Id);
        Assert.AreEqual(DelegatedAccessGrantStatus.Active, persistedGrant.Status);
        Assert.IsNotNull(persistedGrant.AcceptedAt);
        Assert.IsNotNull(persistedGrant.StartsAt);
        Assert.IsNotNull(invitation.ConsumedAt);
    }

    [TestMethod]
    public async Task AcceptGrant_RejectsInvitationReplay()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        await fixture.CreateGrantAsync();
        await fixture.Service.AcceptGrantAsync(fixture.InvitationToken, fixture.DelegateId);

        await Assert.ThrowsExactlyAsync<ConflictError>(() =>
            fixture.Service.AcceptGrantAsync(fixture.InvitationToken, fixture.DelegateId));
    }

    [TestMethod]
    public async Task RevokeGrant_ByDelegateImmediatelyDisablesGrant()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var grant = await fixture.CreateGrantAsync();
        await fixture.Service.AcceptGrantAsync(fixture.InvitationToken, fixture.DelegateId);

        await fixture.Service.RevokeGrantAsync(grant.Id, fixture.DelegateId, "No longer needed");

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.DelegatedAccessGrants.SingleAsync(candidate => candidate.Id == grant.Id);
        Assert.AreEqual(DelegatedAccessGrantStatus.Revoked, persisted.Status);
        Assert.AreEqual(fixture.DelegateId, persisted.RevokedByUserAccountId);
        Assert.AreEqual("No longer needed", persisted.RevocationReason);
    }

    private sealed class LifecycleFixture(
        SqliteConnection connection,
        AuthDbContext db,
        DelegatedAccessGrantService service,
        CapturingEmailSender emailSender,
        Guid tenantId,
        Guid delegatorId,
        Guid delegateId) : IAsyncDisposable
    {
        public AuthDbContext Db { get; } = db;
        public DelegatedAccessGrantService Service { get; } = service;
        public Guid TenantId { get; } = tenantId;
        public Guid DelegatorId { get; } = delegatorId;
        public Guid DelegateId { get; } = delegateId;
        public string InvitationToken => emailSender.InvitationToken
            ?? throw new AssertFailedException("Invitation token was not captured from email.");

        public static async Task<LifecycleFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var tenantId = Guid.NewGuid();
            var delegatorId = Guid.NewGuid();
            var delegateId = Guid.NewGuid();
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Slug = "delegation-lifecycle",
                Name = "Delegation Lifecycle",
                IssuerUri = "https://issuer.example/delegation-lifecycle",
                Status = TenantStatus.Active
            });
            db.UserAccounts.AddRange(
                new UserAccount { Id = delegatorId, Username = "delegator", Email = "delegator@example.test" },
                new UserAccount { Id = delegateId, Username = "delegate", Email = "delegate@example.test" });
            db.UserTenantMemberships.AddRange(
                new UserTenantMembership { UserAccountId = delegatorId, TenantId = tenantId },
                new UserTenantMembership { UserAccountId = delegateId, TenantId = tenantId });
            await db.SaveChangesAsync();

            var emailSender = new CapturingEmailSender();
            var service = new DelegatedAccessGrantService(
                db,
                new DelegableCapabilityCatalog(),
                new UserTenantMembershipService(db),
                new NoopAuditSink(),
                emailSender,
                new UserAccountService(db),
                Options.Create(new DelegationOptions()),
                Options.Create(new AuthOptions { EnableDelegatedAccess = true }),
                NullLogger<DelegatedAccessGrantService>.Instance);
            return new LifecycleFixture(connection, db, service, emailSender, tenantId, delegatorId, delegateId);
        }

        public Task<DelegatedAccessGrant> CreateGrantAsync() => Service.CreateGrantAsync(
            TenantId,
            DelegatorId,
            DelegateId,
            ["profile.read"],
            "Assist with profile review",
            DateTimeOffset.UtcNow.AddHours(1));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public string? InvitationToken { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            const string prefix = "/account/delegated-access/invitations/";
            var start = message.TextBody?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
            if (start >= 0)
            {
                start += prefix.Length;
                var end = message.TextBody!.IndexOf('\n', start);
                InvitationToken = Uri.UnescapeDataString(message.TextBody[start..(end < 0 ? message.TextBody.Length : end)]);
            }
            return Task.CompletedTask;
        }
    }
}