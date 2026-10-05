using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Third 2026-10-04 review: any path ending in /notfound skipped tenant resolution and so the H4 membership check,
/// including admin API routes whose last segment is a caller-chosen value.
/// </summary>
[TestClass]
public sealed class TenantNotFoundPathTests
{
    [TestMethod]
    [DataRow("/t/acme/notfound", true)]
    [DataRow("/t/b/admin/api/scopes/notfound", false)]
    [DataRow("/t/b/register/notfound", false)]
    [DataRow("/t/b/admin/api/clients/123/scopes/notfound", false)]
    public void OnlyTheTenantNotFoundPageIsExempt(string path, bool exempt)
        => Assert.AreEqual(exempt, TenantResolutionMiddleware.IsTenantNotFoundPage(path));
}
