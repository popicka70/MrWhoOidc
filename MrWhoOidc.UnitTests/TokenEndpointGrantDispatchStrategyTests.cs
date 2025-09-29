using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Encodings.Web;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenEndpointGrantDispatchStrategyTests
{
    private const string Issuer = "https://issuer";

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
                    services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase(dbName));
                    services.AddMrWhoOidcAuthCore();
                    services.AddSingleton<OidcMetrics>();
                    services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcMetrics>());
                    services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
                    services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
                    services.AddSingleton<MrWhoOidc.Security.IDPoPValidator, TestCryptoDpopValidator>();
                    services.AddScoped<ITokenHandler, MrWhoOidc.WebAuth.Handlers.TokenHandler>();
                    services.AddScoped<ITokenGrantHandler, RefreshTokenGrantHandler>();
                    // Only register refresh_token handler in this focused dispatch test host to keep scope narrow
                    services.AddSingleton(new OidcOptions { Issuer = Issuer });
                });
                webBuilder.Configure(async app =>
                {
                    using var scope = app.ApplicationServices.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    var hasher = new Argon2PasswordHasher();
                    var realm = new Realm { Name = "default" };
                    db.Realms.Add(realm);
                    db.Clients.Add(new Client
                    {
                        ClientId = clientId,
                        ClientSecretHash = hasher.Hash(clientSecret),
                        RealmId = realm.Id
                    });
                    db.Users.Add(new User { Username = "alice", PasswordHash = "p" });
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
    public async Task RefreshToken_Dispatch_Handled_ByStrategy()
    {
        var dbName = "rt-dispatch-" + Guid.NewGuid().ToString("N");
        var clientId = "c1"; var clientSecret = "secret";
        var host = await CreateHostAsync(dbName, clientId, clientSecret);
        using var hostRef = host;

        string rawRt;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = db.Users.Single();
            var rtSvc = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            (rawRt, _) = await rtSvc.CreateRefreshTokenAsync(user.Id, clientId, TimeSpan.FromDays(1), new[] { "openid" });
        }

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(clientId, clientSecret);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rawRt
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("access_token", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("refresh_token", out _));
    }

    [TestMethod]
    public async Task RefreshToken_MissingToken_ReturnsInvalidRequest()
    {
        var dbName = "rt-dispatch-missing-" + Guid.NewGuid().ToString("N");
        var clientId = "c2"; var clientSecret = "secret";
        var host = await CreateHostAsync(dbName, clientId, clientSecret);
        using var hostRef = host;

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(clientId, clientSecret);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token" // missing refresh_token param
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var err));
        Assert.AreEqual("invalid_request", err.GetString());
    }

}

// Minimal local DPoP validator used only for these tests (reduced logic)
file sealed class TestCryptoDpopValidator : MrWhoOidc.Security.IDPoPValidator
{
    public Task<MrWhoOidc.Security.DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
    {
        // Accept absence; not central to these tests
        var header = http.Request.Headers["DPoP"].ToString();
        if (string.IsNullOrEmpty(header))
            return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(true, null, null, null, null, null));
        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ReadJwtToken(header); // basic parse only
            return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(true, null, null, null, null, null));
        }
        catch
        {
            return Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(false, null, null, null, null, "invalid_dpop"));
        }
    }
}
