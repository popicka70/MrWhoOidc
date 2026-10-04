using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// The /ciba approve handler: C11 binding of the approval to the user the auth_req_id was issued for, V5 user-client
/// assignment, and the H8 MFA re-check that reads TOTP from the global account.
/// </summary>
[TestClass]
public sealed class CibaApprovalPageTests
{
    private static async Task<CibaAuthenticationRequest> ReloadAsync(ApprovalPageHarness h, CibaAuthenticationRequest request)
    {
        h.Db.ChangeTracker.Clear();
        return await h.Db.CibaAuthenticationRequests.SingleAsync(r => r.Id == request.Id);
    }

    [TestMethod]
    public async Task Approve_ByUserOtherThanTheRequestedUser_IsRefused()
    {
        using var h = new ApprovalPageHarness();
        var alice = h.SeedUser("alice");
        var mallory = h.SeedUser("mallory");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", targetUser: alice, clientNotificationToken: "ping-token");
        // Mallory is assigned and MFA-satisfied: only the C11 binding stands between her and tokens for Alice.
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(mallory), request.AuthReqId);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        StringAssert.Contains(page.ErrorMessage, "different user");
        var stored = await ReloadAsync(h, request);
        Assert.AreEqual(CibaRequestStatus.Pending, stored.Status);
        Assert.IsNull(stored.UserId);
        h.CibaNotifications.Verify(n => n.SendPingNotificationAsync(It.IsAny<CibaAuthenticationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Deny_ByUserOtherThanTheRequestedUser_IsRefused()
    {
        using var h = new ApprovalPageHarness();
        var alice = h.SeedUser("alice");
        var mallory = h.SeedUser("mallory");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", targetUser: alice);
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(mallory), request.AuthReqId);

        await page.OnPostAsync("deny");

        Assert.AreEqual(CibaRequestStatus.Pending, (await ReloadAsync(h, request)).Status, "another user must not be able to cancel Alice's request either");
    }

    [TestMethod]
    public async Task Approve_RequestedUserNotAssignedToClient_IsRefused()
    {
        using var h = new ApprovalPageHarness();
        var alice = h.SeedUser("alice");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", targetUser: alice);
        h.SetAssigned(false);
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(alice), request.AuthReqId);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsFalse(string.IsNullOrEmpty(page.ErrorMessage));
        Assert.IsFalse(page.ShowSuccess);
        var stored = await ReloadAsync(h, request);
        Assert.AreEqual(CibaRequestStatus.Pending, stored.Status, "an unassigned user must not authorize the request");
        Assert.IsNull(stored.UserId);
    }

    [TestMethod]
    public async Task Approve_RequestedAndAssignedUser_AuthorizesAndPingsClient()
    {
        using var h = new ApprovalPageHarness();
        var alice = h.SeedUser("alice");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", targetUser: alice, clientNotificationToken: "ping-token");
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(alice), request.AuthReqId);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsNull(page.ErrorMessage);
        Assert.IsTrue(page.ShowSuccess);
        var stored = await ReloadAsync(h, request);
        Assert.AreEqual(CibaRequestStatus.Authorized, stored.Status);
        Assert.AreEqual(alice.Id, stored.UserId);
        Assert.IsTrue(stored.PingNotificationSent);
        h.CibaNotifications.Verify(n => n.SendPingNotificationAsync(It.Is<CibaAuthenticationRequest>(r => r.Id == request.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Approve_AccountTotpEnabledButSessionWithoutMfa_RedirectsToTotp()
    {
        using var h = new ApprovalPageHarness();
        // H8: TOTP enrolled on the global account; the per-tenant legacy flag stays false.
        var alice = h.SeedUser("alice", accountTotp: true, tenantUserTotp: false);
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", targetUser: alice);
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(alice, mfa: false), request.AuthReqId);

        var result = await page.OnPostAsync("approve");

        var redirect = Assert.IsInstanceOfType<RedirectResult>(result);
        StringAssert.StartsWith(redirect.Url, "/LoginTotp");
        h.VerifyPreauthIssued();
        var stored = await ReloadAsync(h, request);
        Assert.AreEqual(CibaRequestStatus.Pending, stored.Status, "approval must wait for the second factor");
        Assert.IsNull(stored.UserId);
    }
}
