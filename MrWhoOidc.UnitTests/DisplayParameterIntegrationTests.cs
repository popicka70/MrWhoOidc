using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class DisplayParameterIntegrationTests
{
    // Shared fixture eliminates per-test WebApplicationFactory creation overhead
    private static SharedWebAppFixture _fixture = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new SharedWebAppFixture();

    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    [TestMethod]
    public async Task Login_When_DisplayPopup_Enables_Popup_Layout_Otherwise_Default()
    {
        using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var popupResp = await client.GetAsync("/login?display=popup");
        Assert.AreEqual(HttpStatusCode.OK, popupResp.StatusCode);

        var popupHtml = await popupResp.Content.ReadAsStringAsync();
        StringAssert.Contains(popupHtml, "auth-display-popup");
        StringAssert.Contains(popupHtml, "data-auth-display=\"popup\"");

        var pageResp = await client.GetAsync("/login?display=page");
        Assert.AreEqual(HttpStatusCode.OK, pageResp.StatusCode);

        var pageHtml = await pageResp.Content.ReadAsStringAsync();
        Assert.IsFalse(pageHtml.Contains("auth-display-popup", System.StringComparison.Ordinal));
    }
}
