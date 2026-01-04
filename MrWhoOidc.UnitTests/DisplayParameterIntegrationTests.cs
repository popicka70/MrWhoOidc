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
    [TestMethod]
    public async Task Login_When_DisplayPopup_Enables_Popup_Layout_Otherwise_Default()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
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
