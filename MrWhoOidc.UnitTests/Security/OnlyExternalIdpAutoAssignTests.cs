using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// AutoApprovalMode.OnlyExternalIdp (2026-10-04 assessment) must not auto-assign local sign-ins (idp="local").
/// </summary>
[TestClass]
public sealed class OnlyExternalIdpAutoAssignTests
{
    [TestMethod]
    [DataRow("local", false)]
    [DataRow("LOCAL", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("google", true)]
    public async Task EnsureAssigned_OnlyExternalIdp_AutoAssignsExternalSessionsOnly(string? idp, bool expected)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId, ClientId = "web", ClientName = "Web", RealmId = Guid.NewGuid(),
            AutoApprovalMode = AutoApprovalMode.OnlyExternalIdp
        };
        var user = new User { TenantId = tenantId, Username = "alice" };
        db.Users.Add(user);
        db.SaveChanges();
        var clients = new Mock<IClientStore>();
        clients.Setup(c => c.FindByClientIdAsync("web", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        var svc = new UserClientAssignmentService(db, clients.Object, NullLogger<UserClientAssignmentService>.Instance);

        var (assigned, _) = await svc.EnsureAssignedAsync(user.Id, "web", idp);

        Assert.AreEqual(expected, assigned);
        Assert.AreEqual(expected, db.UserClientAssignments.Any(a => a.UserId == user.Id));
    }
}
