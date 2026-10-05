using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth.Infrastructure.Security;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class QrHttpSecurityTests
{
    private const string SessionToken = "http-qr-session";
    private const string InitiatorSecret = "http-initiator-secret";

    [TestMethod]
    public async Task QrDesktopForm_ProtectsCancellation_AndCompletionIsPostOnly()
    {
        var qr = new Mock<IQrLoginService>();
        qr.Setup(q => q.GetSessionAsync(SessionToken)).ReturnsAsync(new QrLoginSession
        {
            SessionToken = SessionToken,
            SessionTokenHash = CryptoHelper.ComputeSha256Hex(SessionToken),
            InitiatorSecretHash = CryptoHelper.ComputeSha256Hex(InitiatorSecret),
            ClientId = "platform",
            ReturnUrl = "/",
            Status = QrSessionStatus.Pending,
            MatchCode = "42",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
        });
        qr.Setup(q => q.BuildMobileUrl(SessionToken)).Returns($"https://localhost/auth/qr-mobile?session={SessionToken}");
        qr.Setup(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Cancelled, null, null)).ReturnsAsync(true);

        using var factory = TestWebAppFactory.CreateInMemory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IQrLoginService>(_ => qr.Object));
                services.Configure<QrLoginOptions>(options => options.Enabled = true);
            }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{QrInitiatorBinding.CookieName(SessionToken)}={InitiatorSecret}");

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var completion = endpoints.Single(e => e.RoutePattern.RawText == "/auth/qr-complete");
        CollectionAssert.AreEqual(new[] { "POST" }, completion.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray());

        using var page = await client.GetAsync($"/auth/qr?token={SessionToken}");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "id=\"completeForm\" method=\"post\"");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.IsTrue(match.Success, "Desktop page must render an antiforgery token.");
        var token = WebUtility.HtmlDecode(match.Groups[1].Value);

        using var get = await client.GetAsync($"/auth/qr-complete?session={SessionToken}");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        foreach (var path in new[] { $"/auth/qr-complete?session={SessionToken}", "/api/qr/cancel", "/api/qr/confirm" })
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["sessionToken"] = SessionToken });
            using var refused = await client.PostAsync(path, content);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, path);
        }
        qr.Verify(q => q.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<QrSessionStatus>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);

        using var cancelContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionToken"] = SessionToken,
            ["__RequestVerificationToken"] = token
        });
        using var cancelled = await client.PostAsync("/api/qr/cancel", cancelContent);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Cancelled, null, null), Times.Once);
    }
}
