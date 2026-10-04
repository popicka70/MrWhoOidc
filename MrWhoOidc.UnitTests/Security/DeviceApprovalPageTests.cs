using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// The /device approve handler (RFC 8628): V5 user-client assignment, the CLI system client's admin gate, and the
/// H8 MFA re-check that reads TOTP from the global account rather than the per-tenant flag.
/// </summary>
[TestClass]
public sealed class DeviceApprovalPageTests
{
    private const string UserCode = "ABCDEFGH";

    private static async Task<DeviceCodeEntry> ReloadAsync(ApprovalPageHarness h, DeviceCodeEntry entry)
    {
        h.Db.ChangeTracker.Clear();
        return await h.Db.DeviceCodes.SingleAsync(d => d.Id == entry.Id);
    }

    [TestMethod]
    public async Task Approve_UserNotAssignedToClient_IsRefused()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("tv-app");
        var entry = h.SeedDeviceCode("tv-app", UserCode);
        h.SetAssigned(false);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user), UserCode);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsFalse(string.IsNullOrEmpty(page.ErrorMessage));
        Assert.IsFalse(page.ShowSuccess);
        var stored = await ReloadAsync(h, entry);
        Assert.AreEqual(DeviceCodeStatus.Pending, stored.Status, "an unassigned user must not authorize the device");
        Assert.IsNull(stored.UserId);
    }

    [TestMethod]
    public async Task Approve_AssignedUser_AuthorizesDeviceForThatUser()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("tv-app");
        var entry = h.SeedDeviceCode("tv-app", UserCode);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user), "abcd-efgh");

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsNull(page.ErrorMessage);
        Assert.IsTrue(page.ShowSuccess);
        var stored = await ReloadAsync(h, entry);
        Assert.AreEqual(DeviceCodeStatus.Authorized, stored.Status);
        Assert.AreEqual(user.Id, stored.UserId);
    }

    [TestMethod]
    public async Task Approve_SystemClientWithoutAdminPolicy_IsRefused()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("mrwho-cli", isSystemClient: true);
        var entry = h.SeedDeviceCode("mrwho-cli", UserCode);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user), UserCode);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        StringAssert.Contains(page.ErrorMessage, "administrator");
        var stored = await ReloadAsync(h, entry);
        Assert.AreEqual(DeviceCodeStatus.Pending, stored.Status, "CLI access requires tenant-admin or platform-admin");
        Assert.IsNull(stored.UserId);
    }

    [TestMethod]
    [DataRow("tenant-admin")]
    [DataRow("platform-admin")]
    public async Task Approve_SystemClientWithAdminPolicy_Authorizes(string policy)
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("mrwho-cli", isSystemClient: true);
        var entry = h.SeedDeviceCode("mrwho-cli", UserCode);
        h.GrantPolicies(policy);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user), UserCode);

        await page.OnPostAsync("approve");

        Assert.AreEqual(DeviceCodeStatus.Authorized, (await ReloadAsync(h, entry)).Status);
    }

    [TestMethod]
    public async Task Approve_AccountTotpEnabledButSessionWithoutMfa_RedirectsToTotp()
    {
        using var h = new ApprovalPageHarness();
        // H8: TOTP enrolled on the global account; the per-tenant legacy flag stays false.
        var user = h.SeedUser("alice", accountTotp: true, tenantUserTotp: false);
        h.SeedClient("tv-app");
        var entry = h.SeedDeviceCode("tv-app", UserCode);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user, mfa: false), UserCode);

        var result = await page.OnPostAsync("approve");

        var redirect = Assert.IsInstanceOfType<RedirectResult>(result);
        StringAssert.StartsWith(redirect.Url, "/LoginTotp");
        h.VerifyPreauthIssued();
        var stored = await ReloadAsync(h, entry);
        Assert.AreEqual(DeviceCodeStatus.Pending, stored.Status, "approval must wait for the second factor");
        Assert.IsNull(stored.UserId);
    }
}
