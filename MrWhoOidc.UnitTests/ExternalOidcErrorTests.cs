using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using System;
using System.Threading.Tasks;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ExternalOidcErrorTests
{
    private static (IExternalOidcHandler handler, DefaultHttpContext ctx, IServiceScope scope) Create()
    {
        var (scope, handler, ctx) = ExternalOidcTestHost.Create(
            configureServices: services =>
            {
                services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions()));
            },
            inMemoryDbName: "ext-err-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: false,
            useRecordingMetrics: false);

        return (handler, ctx, scope);
    }

    [TestMethod]
    public async Task Start_UnknownProvider_ReturnsRedirectToErrorPage()
    {
        var (h, ctx, scope) = Create();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?provider=doesnotexist&returnUrl=%2Fauthorize%3Fclient_id%3Dweb&clientId=web");
            var result = await h.StartAsync(ctx);

            Assert.IsNotNull(result);
            var prop = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var urlVal = prop?.GetValue(result)?.ToString();

            Assert.IsTrue(
                urlVal?.Contains("/auth/external/error", StringComparison.OrdinalIgnoreCase) ?? false,
                "Expected redirect to external error page");
        }
    }
}
