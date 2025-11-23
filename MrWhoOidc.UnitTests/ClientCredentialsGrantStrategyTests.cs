using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Services;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
using Microsoft.Extensions.Hosting;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using MrWhoOidc.UnitTests.TestDoubles;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class ClientCredentialsGrantStrategyTests
{
    private const string Issuer = "https://issuer";
    private static readonly Guid DefaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");

    private static AuthenticationHeaderValue Basic(string id, string secret)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    private static async Task<IHost> CreateHostAsync(string dbName, string clientId, string clientSecret)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
                    services.AddMrWhoOidcAuthCore();

                    // Override ITenantAccessor with test implementation that automatically sets default tenant
                    services.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor>(sp =>
                    {
                        var db = sp.GetRequiredService<AuthDbContext>();
                        var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MrWhoOidc.UnitTests.Testing.TestTenantAccessor>>();
                        return new MrWhoOidc.UnitTests.Testing.TestTenantAccessor(db, DefaultTenantId, logger);
                    });

                    services.AddSingleton<OidcMetrics>();
                    services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcMetrics>());
                    services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
                    services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
                    services.AddScoped<IClientAuthenticator, ClientAuthenticator>();
                    services.AddSingleton<MrWhoOidc.Security.IDPoPValidator, TestCryptoDpopValidator>();
                    services.AddSingleton<IFeatureService, StubFeatureService>();
                    services.AddScoped<ITokenHandler, MrWhoOidc.WebAuth.Handlers.TokenHandler>();
                    services.AddScoped<ITokenGrantHandler, RefreshTokenGrantHandler>();
                    services.AddScoped<ITokenGrantHandler, AuthorizationCodeGrantHandler>();
                    services.AddScoped<ITokenGrantHandler, ClientCredentialsGrantHandler>();
                    services.AddSingleton(new OidcOptions { Issuer = Issuer });
                });
                webBuilder.Configure(async app =>
                {
                    using var scope = app.ApplicationServices.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    var hasher = new Argon2PasswordHasher();

                    // Seed default tenant
                    var tenant = new Tenant
                    {
                        Id = DefaultTenantId,
                        Slug = "default",
                        Name = "Default Tenant",
                        IssuerUri = Issuer,
                        Status = TenantStatus.Active,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.Tenants.Add(tenant);

                    var realm = new Realm { Name = "default", TenantId = DefaultTenantId };
                    db.Realms.Add(realm);
                    db.Clients.Add(new ClientEntity
                    {
                        ClientId = clientId,
                        ClientSecretHash = hasher.Hash(clientSecret),
                        RealmId = realm.Id,
                        TenantId = DefaultTenantId
                    });
                    await db.SaveChangesAsync();

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx));
                    });
                });
            });

        return await builder.StartAsync();
    }

    [TestMethod]
    public async Task ClientCredentials_Handled_ByStrategy()
    {
        var dbName = "cc-strat-" + Guid.NewGuid().ToString("N");
        var clientId = "m2m"; var clientSecret = "secret";
        var host = await CreateHostAsync(dbName, clientId, clientSecret);
        using var hostRef = host;

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(clientId, clientSecret);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["audience"] = "api"
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, await resp.Content.ReadAsStringAsync());
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("access_token", out _));
        Assert.AreEqual("Bearer", doc.RootElement.GetProperty("token_type").GetString());
    }

    [TestMethod]
    public async Task ClientCredentials_AudienceResourceConflict_InvalidRequest()
    {
        var dbName = "cc-strat-conflict-" + Guid.NewGuid().ToString("N");
        var clientId = "m2m2"; var clientSecret = "secret";
        var host = await CreateHostAsync(dbName, clientId, clientSecret);
        using var hostRef = host;

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(clientId, clientSecret);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["audience"] = "api-a",
            ["resource"] = "api-b"
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_request", doc.RootElement.GetProperty("error").GetString());
    }
}

// Reuse minimal DPoP validator
file sealed class TestCryptoDpopValidator : MrWhoOidc.Security.IDPoPValidator
{
    public Task<MrWhoOidc.Security.DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        => Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(true, null, null, null, null, null));
}
