using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.Infrastructure.Pipeline;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ForwardedHeadersSecurityTests
{
    [TestMethod]
    public async Task ForwardedHeaders_Disallows_Unconfigured_XForwardedHost()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.UseEnvironment(Environments.Production);
                webBuilder.ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Ensure the default AllowedHosts behavior is exercised.
                        ["Oidc:Issuer"] = "https://localhost:7208",
                        ["ForwardedHeaders:Enabled"] = "true"
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    var cfg = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
                    var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("ForwardedHeadersTest");

                    Assert.IsTrue(ForwardedHeadersConfigurator.TryBuild(cfg, env, logger, out ForwardedHeadersOptions options));

                    // If an issuer is configured and no explicit AllowedHosts list is provided,
                    // the issuer host should be used to prevent X-Forwarded-Host spoofing.
                    CollectionAssert.Contains(options.AllowedHosts.ToList(), "localhost", "Expected AllowedHosts to include issuer host 'localhost'.");

                    app.UseForwardedHeaders(options);
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/whoami", async ctx =>
                        {
                            await ctx.Response.WriteAsync(ctx.Request.Host.Value ?? string.Empty);
                        });
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.example");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        // Spoofed host should be ignored.
        StringAssert.StartsWith(body, "localhost", $"Expected Request.Host to remain 'localhost*', got '{body}'.");
    }
}
