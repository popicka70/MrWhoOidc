using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

/// <summary>
/// R2: a tenant admin could mark any domain claim verified (and so auto-join every user of, say, a competitor's
/// domain). Verification now requires the claim's token in a TXT record at _mrwho-challenge.&lt;domain&gt;; only a
/// platform admin can override, with an audited reason.
/// </summary>
[TestClass]
public sealed class TenantDomainClaimDnsVerificationTests
{
    [TestMethod]
    public async Task NewClaim_GetsAPerClaimDnsChallenge_AndStartsPending()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var service = CreateService(db, new FakeDns());

        var first = (await service.CreateClaimAsync(tenant.Id, "Example.com", TenantDomainEnrollmentMode.AutoJoin, null, null)).Claim;
        var second = (await service.CreateClaimAsync(tenant.Id, "example.org", TenantDomainEnrollmentMode.AutoJoin, null, null)).Claim;

        Assert.AreEqual(TenantDomainClaimStatus.PendingVerification, first.Status);
        Assert.AreEqual("_mrwho-challenge.example.com", first.VerificationDnsName);
        Assert.AreEqual("mrwho-domain-verification=" + first.VerificationToken, first.VerificationDnsValue);
        Assert.IsGreaterThanOrEqualTo(40, first.VerificationToken!.Length, "256-bit token");
        Assert.AreNotEqual(first.VerificationToken, second.VerificationToken);
        Assert.AreEqual(TenantDomainClaimStatus.PendingVerification, new TenantDomainClaim().Status, "the entity default fails closed");
    }

    [TestMethod]
    public async Task Verify_WithoutTheTxtRecord_StaysPending_AndDoesNotAutoJoin()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var dns = new FakeDns();
        var service = CreateService(db, dns);
        var claim = (await service.CreateClaimAsync(tenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null)).Claim;
        dns.Records["_mrwho-challenge.example.com"] = ["mrwho-domain-verification=someone-elses-token", "v=spf1 -all"];
        dns.Records["example.com"] = [claim.VerificationDnsValue!]; // wrong name: not the challenge label

        var result = await service.VerifyClaimAsync(tenant.Id, claim.Id);

        Assert.AreEqual(TenantDomainClaimVerificationOutcome.RecordNotFound, result.Outcome);
        Assert.AreEqual(claim.VerificationDnsName, result.DnsName);
        Assert.AreEqual(claim.VerificationDnsValue, result.DnsValue);
        Assert.AreEqual(TenantDomainClaimStatus.PendingVerification, (await db.TenantDomainClaims.SingleAsync()).Status);
        Assert.IsNull(await service.ResolveAutoJoinClaimAsync("user@example.com"));
    }

    [TestMethod]
    public async Task Verify_WithTheTxtRecord_VerifiesAndEnablesAutoJoin()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var dns = new FakeDns();
        var audit = new RecordingAudit();
        var service = CreateService(db, dns, audit);
        var claim = (await service.CreateClaimAsync(tenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null)).Claim;
        dns.Records["_mrwho-challenge.example.com"] = ["v=spf1 -all", "  " + claim.VerificationDnsValue + " "];

        Assert.AreEqual(TenantDomainClaimVerificationOutcome.NotFound, (await service.VerifyClaimAsync(Guid.NewGuid(), claim.Id)).Outcome, "another tenant cannot verify it");
        var result = await service.VerifyClaimAsync(tenant.Id, claim.Id);

        Assert.AreEqual(TenantDomainClaimVerificationOutcome.Verified, result.Outcome);
        Assert.AreEqual(tenant.Id, (await service.ResolveAutoJoinClaimAsync("user@example.com"))?.TenantId);
        Assert.AreEqual("tenant_domain_claim.verified", audit.Events.Single().Type);
    }

    [TestMethod]
    public async Task Verify_LegacyClaimWithoutChallenge_IssuesOneInsteadOfVerifying()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        db.TenantDomainClaims.Add(new TenantDomainClaim
        {
            TenantId = tenant.Id,
            Domain = "example.com",
            NormalizedDomain = "example.com",
            Status = TenantDomainClaimStatus.PendingVerification
        });
        await db.SaveChangesAsync();
        var claimId = (await db.TenantDomainClaims.SingleAsync()).Id;
        var service = CreateService(db, new FakeDns());

        var result = await service.VerifyClaimAsync(tenant.Id, claimId);

        Assert.AreEqual(TenantDomainClaimVerificationOutcome.RecordNotFound, result.Outcome);
        Assert.AreEqual("_mrwho-challenge.example.com", result.DnsName);
        StringAssert.StartsWith(result.DnsValue, "mrwho-domain-verification=");
        Assert.AreEqual(TenantDomainClaimStatus.PendingVerification, (await db.TenantDomainClaims.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task ManualOverride_RequiresAReason_AndIsAudited()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var audit = new RecordingAudit();
        var service = CreateService(db, new FakeDns(), audit);
        var claim = (await service.CreateClaimAsync(tenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null)).Claim;
        var actor = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.MarkClaimVerifiedManuallyAsync(claim.Id, actor, "ops", " "));
        Assert.IsTrue(await service.MarkClaimVerifiedManuallyAsync(claim.Id, actor, "ops", "ticket OPS-42: registrar confirmed ownership"));

        Assert.AreEqual(TenantDomainClaimStatus.Verified, (await db.TenantDomainClaims.SingleAsync()).Status);
        var (type, payload) = audit.Events.Single();
        Assert.AreEqual("tenant_domain_claim.verified", type);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        StringAssert.Contains(json, "manual_override");
        StringAssert.Contains(json, actor.ToString());
        StringAssert.Contains(json, "OPS-42");
    }

    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ITenantDomainClaimService CreateService(AuthDbContext db, IDnsTxtResolver dns, MrWhoOidc.Auth.Observability.IAuditSink? audit = null)
        => new TenantDomainClaimService(
            db,
            NullLogger<TenantDomainClaimService>.Instance,
            Options.Create(new PublicEmailDomainOptions()),
            dns,
            audit);

    private static async Task<Tenant> SeedTenantAsync(AuthDbContext db)
    {
        var tenant = new Tenant { Slug = "acme", Name = "Acme", IssuerUri = "https://localhost/t/acme", Status = TenantStatus.Active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private sealed class FakeDns : IDnsTxtResolver
    {
        public Dictionary<string, string[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Records.TryGetValue(name, out var values) ? values : []);
    }

    private sealed class RecordingAudit : MrWhoOidc.Auth.Observability.IAuditSink
    {
        public List<(string Type, object Payload)> Events { get; } = [];

        public void Emit(string type, object payload) => Events.Add((type, payload));

        public string? HashValue(string? value) => value;
    }
}
