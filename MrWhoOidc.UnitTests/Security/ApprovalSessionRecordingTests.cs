using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// F1: a session that was only used by the device flow or by CIBA knows the client that took part. The approval
/// happens in the browser, so the browser knows the session: the approval records it and registers the client.
/// </summary>
[TestClass]
public sealed class ApprovalSessionRecordingTests
{
    private const string UserCode = "ABCDEFGH";

    [TestMethod]
    public async Task TheDeviceApproval_RecordsTheSession_AndRegistersTheClient()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("tv-app");
        var entry = h.SeedDeviceCode("tv-app", UserCode);
        var session = await h.CreateSessionAsync(user);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user, sessionId: session.Id), UserCode);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsTrue(page.ShowSuccess);

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.DeviceCodes.SingleAsync(d => d.Id == entry.Id);
        Assert.AreEqual(session.Id, stored.SessionId, "the device belongs to the session that approved it");

        var clients = await h.Sessions.GetClientIdsAsync(session.Id);
        CollectionAssert.Contains(clients.ToList(), "tv-app", "the session-scoped logout notifies the device client");
    }

    [TestMethod]
    public async Task TheDeviceApproval_WithoutASession_BehavesAsBefore()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("tv-app");
        var entry = h.SeedDeviceCode("tv-app", UserCode);
        var page = h.CreateDeviceModel(ApprovalPageHarness.SessionFor(user), UserCode);

        await page.OnPostAsync("approve");

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.DeviceCodes.SingleAsync(d => d.Id == entry.Id);
        Assert.IsNull(stored.SessionId, "a browser without a session leaves the device unbound");
    }

    [TestMethod]
    public async Task TheCibaApproval_RecordsTheSession_AndRegistersTheClient()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", user);
        var session = await h.CreateSessionAsync(user);
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(user, sessionId: session.Id), request.AuthReqId);

        var result = await page.OnPostAsync("approve");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsTrue(page.ShowSuccess);

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.CibaAuthenticationRequests.SingleAsync(r => r.Id == request.Id);
        Assert.AreEqual(session.Id, stored.SessionId, "the CIBA request belongs to the session that approved it");

        var clients = await h.Sessions.GetClientIdsAsync(session.Id);
        CollectionAssert.Contains(clients.ToList(), "bank-app", "the session-scoped logout notifies the CIBA client");
    }

    [TestMethod]
    public async Task TheCibaApproval_WithoutASession_BehavesAsBefore()
    {
        using var h = new ApprovalPageHarness();
        var user = h.SeedUser("alice");
        h.SeedClient("bank-app");
        var request = h.SeedCibaRequest("bank-app", user);
        var page = h.CreateCibaModel(ApprovalPageHarness.SessionFor(user), request.AuthReqId);

        await page.OnPostAsync("approve");

        h.Db.ChangeTracker.Clear();
        var stored = await h.Db.CibaAuthenticationRequests.SingleAsync(r => r.Id == request.Id);
        Assert.IsNull(stored.SessionId, "a browser without a session leaves the request unbound");
    }
}
