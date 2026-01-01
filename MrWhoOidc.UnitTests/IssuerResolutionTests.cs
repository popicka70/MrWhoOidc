using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class IssuerResolutionTests
{
    [TestMethod]
    public async Task GetIssuer_Rewrites_AbsoluteTenantIssuer_ToConfiguredPublicBaseUrl()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.UseEnvironment(Environments.Production);
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddScoped<ITenantAccessor, TenantAccessor>();
                    services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions { Enabled = true });
                    services.AddScoped<IIssuerBuilder, IssuerBuilder>();

                    // Simulate canonical cloud/public URL being configured.
                    services.AddSingleton(new OidcOptions
                    {
                        PublicBaseUrl = "https://mrwho.onrender.com",
                        Issuer = null
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/issuer", async ctx =>
                        {
                            var tenantAccessor = ctx.RequestServices.GetRequiredService<ITenantAccessor>();
                            tenantAccessor.SetTenant(new TenantContext
                            {
                                TenantId = Guid.NewGuid(),
                                Slug = "default",
                                Name = "Default",
                                IssuerUri = "https://localhost:7157/t/default",
                                IsMultiTenantMode = true
                            });

                            var options = ctx.RequestServices.GetRequiredService<OidcOptions>();
                            await ctx.Response.WriteAsync(ctx.GetIssuer(options));
                        });
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var resp = await client.GetAsync("/issuer");
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadAsStringAsync()).Trim();

        Assert.AreEqual("https://mrwho.onrender.com/t/default", body);
    }

    [TestMethod]
    public async Task GetIssuer_Expands_RelativeTenantIssuer_UsingPublicBaseUrl()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.UseEnvironment(Environments.Production);
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddScoped<ITenantAccessor, TenantAccessor>();
                    services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions { Enabled = true });
                    services.AddScoped<IIssuerBuilder, IssuerBuilder>();
                    services.AddSingleton(new OidcOptions
                    {
                        PublicBaseUrl = "https://mrwho.onrender.com",
                        Issuer = null
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/issuer", async ctx =>
                        {
                            var tenantAccessor = ctx.RequestServices.GetRequiredService<ITenantAccessor>();
                            tenantAccessor.SetTenant(new TenantContext
                            {
                                TenantId = Guid.NewGuid(),
                                Slug = "default",
                                Name = "Default",
                                IssuerUri = "/t/default",
                                IsMultiTenantMode = true
                            });

                            var options = ctx.RequestServices.GetRequiredService<OidcOptions>();
                            await ctx.Response.WriteAsync(ctx.GetIssuer(options));
                        });
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var resp = await client.GetAsync("/issuer");
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadAsStringAsync()).Trim();

        Assert.AreEqual("https://mrwho.onrender.com/t/default", body);
    }
}
