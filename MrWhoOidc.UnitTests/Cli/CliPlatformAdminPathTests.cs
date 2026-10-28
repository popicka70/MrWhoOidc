using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.UnitTests.Cli;

/// <summary>
/// The admin API is tenant-scoped (/t/{slug}/admin/api/...), but the platform-admin API is not: it lives at
/// the server root (/platform-admin/api/...). A profile that points at a tenant-scoped server URL must
/// therefore address the platform-admin API at the root, otherwise every platform command answers 404.
/// </summary>
[TestClass]
public sealed class CliPlatformAdminPathTests
{
    [TestMethod]
    public void GetRootServerUrl_StripsTheTenantPrefix()
    {
        Assert.AreEqual(
            "https://localhost:8443",
            CliServerConnection.GetRootServerUrl("https://localhost:8443/t/default"));
    }

    [TestMethod]
    public void GetRootServerUrl_KeepsARootServerUrl()
    {
        Assert.AreEqual(
            "https://localhost:8443",
            CliServerConnection.GetRootServerUrl("https://localhost:8443"));
    }

    [TestMethod]
    public void GetRootServerUrl_KeepsThePathOfANonTenantServer()
    {
        Assert.AreEqual(
            "https://localhost:8443/oidc",
            CliServerConnection.GetRootServerUrl("https://localhost:8443/oidc"));
    }

    [TestMethod]
    public void CombineRelativePath_AddressesThePlatformAdminApiAtTheRoot()
    {
        var url = CliServerConnection.CombineRelativePath(
            "https://localhost:8443/t/default",
            "platform-admin/api/domain-claims/123/verify-manually");

        Assert.AreEqual("https://localhost:8443/platform-admin/api/domain-claims/123/verify-manually", url);
    }

    [TestMethod]
    public void CombineRelativePath_KeepsTheTenantPrefixForTheTenantAdminApi()
    {
        var url = CliServerConnection.CombineRelativePath(
            "https://localhost:8443/t/default",
            "admin/api/clients");

        Assert.AreEqual("https://localhost:8443/t/default/admin/api/clients", url);
    }
}
