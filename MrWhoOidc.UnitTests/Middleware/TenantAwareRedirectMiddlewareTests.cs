using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests.Middleware;

[TestClass]
public sealed class TenantAwareRedirectMiddlewareTests
{
    [TestMethod]
    [DataRow("/Error")]
    [DataRow("/Error/details")]
    [DataRow("/NotFound")]
    [DataRow("/NotFound/details")]
    public void ShouldSkipRedirect_ErrorRoutes_ReturnsTrue(string path)
    {
        Assert.IsTrue(TenantAwareRedirectMiddleware.ShouldSkipRedirect(path));
    }
}