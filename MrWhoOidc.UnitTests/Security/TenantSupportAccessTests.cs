using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class TenantSupportAccessTests
{
    [TestMethod]
    public void DelegatedAccess_IsDisabledByDefault()
    {
        Assert.IsFalse(new AuthOptions().EnableDelegatedAccess);
    }

    [DataTestMethod]
    [DataRow("POST")]
    [DataRow("PUT")]
    [DataRow("PATCH")]
    [DataRow("DELETE")]
    public void ReadOnlyFilter_BlocksUnsafeAdminPageMethods(string method)
    {
        Assert.IsTrue(SupportAccessReadOnlyPageFilter.ShouldBlock("/Admin/Clients/Add", method));
        Assert.IsTrue(SupportAccessReadOnlyPageFilter.ShouldBlock("/admin/users/edit", method));
    }

    [DataTestMethod]
    [DataRow("GET")]
    [DataRow("HEAD")]
    [DataRow("OPTIONS")]
    public void ReadOnlyFilter_AllowsSafeAdminPageMethods(string method)
    {
        Assert.IsFalse(SupportAccessReadOnlyPageFilter.ShouldBlock("/Admin/Clients", method));
    }

    [TestMethod]
    public void ReadOnlyFilter_DoesNotAffectNonAdminPages()
    {
        Assert.IsFalse(SupportAccessReadOnlyPageFilter.ShouldBlock("/Account/Profile", "POST"));
        Assert.IsFalse(SupportAccessReadOnlyPageFilter.ShouldBlock("/PlatformAdmin/Tenants", "POST"));
    }

    [TestMethod]
    public void TenantAdminOperation_UsesExplicitClassification()
    {
        var requirement = new TenantAdminOperationRequirement
        {
            Kind = TenantAdminOperationKind.SecuritySensitiveWrite
        };

        Assert.AreEqual(
            TenantAdminOperationKind.SecuritySensitiveWrite,
            TenantAdminAuthorizationHandler.ResolveOperationKind(requirement, null));
    }

    [TestMethod]
    public void TenantAdminOperation_ClassifiesUnannotatedSafeRequestAsRead()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;

        Assert.AreEqual(
            TenantAdminOperationKind.Read,
            TenantAdminAuthorizationHandler.ResolveOperationKind(new TenantAdminRequirement(), context));
    }

    [TestMethod]
    public void TenantAdminOperation_ClassifiesUnannotatedMutationAsWrite()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;

        Assert.AreEqual(
            TenantAdminOperationKind.Write,
            TenantAdminAuthorizationHandler.ResolveOperationKind(new TenantAdminRequirement(), context));
    }

    [TestMethod]
    public void TenantAdminOperation_ClassifiesMissingHttpContextAsWrite()
    {
        Assert.AreEqual(
            TenantAdminOperationKind.Write,
            TenantAdminAuthorizationHandler.ResolveOperationKind(new TenantAdminRequirement(), null));
    }

    [TestMethod]
    public async Task Store_CreateAndGet_RequiresMatchingTenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var store = CreateStore(db, tenantId);
        var session = CreateSession(tenantId);

        await store.CreateAsync(session);

        Assert.IsNotNull(await store.GetByIdAsync(session.Id, tenantId));
        Assert.IsNull(await store.GetByIdAsync(session.Id, otherTenantId));
    }

    [TestMethod]
    public async Task Store_RevokeImmediatelyRemovesSessionFromActiveResults()
    {
        var tenantId = Guid.NewGuid();
        var revokerId = Guid.NewGuid();
        await using var db = CreateDb();
        var store = CreateStore(db, tenantId);
        var session = CreateSession(tenantId);
        await store.CreateAsync(session);

        await store.RevokeAsync(session.Id, revokerId, "Security response");

        var persisted = await db.TenantSupportAccessSessions.SingleAsync(x => x.Id == session.Id);
        Assert.AreEqual(SupportAccessStatus.Revoked, persisted.Status);
        Assert.AreEqual(revokerId, persisted.RevokedByUserAccountId);
        Assert.AreEqual("Security response", persisted.RevocationReason);
        Assert.IsNotNull(persisted.RevokedAt);
        Assert.HasCount(0, await store.GetActiveSessionsAsync(tenantId));
    }

    [TestMethod]
    public async Task Store_ActiveResultsExcludeExpiredSessions()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var store = CreateStore(db, tenantId);
        var expired = CreateSession(tenantId);
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.CreateAsync(expired);

        Assert.HasCount(0, await store.GetActiveSessionsAsync(tenantId));
    }

    private static AuthDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }

    private static TenantSupportAccessStore CreateStore(AuthDbContext db, Guid tenantId)
        => new(
            db,
            MockTenantAccessor.CreateWithTenant(tenantId, "test"),
            NullLogger<TenantSupportAccessStore>.Instance);

    private static TenantSupportAccessSession CreateSession(Guid tenantId) => new()
    {
        PlatformAdminUserAccountId = Guid.NewGuid(),
        TenantId = tenantId,
        Mode = SupportAccessMode.ReadOnly,
        Status = SupportAccessStatus.Active,
        Reason = "Troubleshooting",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
    };
}
