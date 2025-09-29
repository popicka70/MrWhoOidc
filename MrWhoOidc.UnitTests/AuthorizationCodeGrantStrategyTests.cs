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

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizationCodeGrantStrategyTests
{
    private const string Issuer = "https://issuer";

    private static AuthenticationHeaderValue Basic(string id, string secret)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    private static async Task<(IHost Host, string Code)> CreateHostAndCodeAsync()
    {
        var dbName = "ac-strat-" + Guid.NewGuid().ToString("N");
        var clientId = "c1"; var clientSecret = "secret";

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
                    services.AddScoped<ITokenGrantHandler, AuthorizationCodeGrantHandler>();
                    services.AddSingleton(new OidcOptions { Issuer = Issuer });
                });
                webBuilder.Configure(async app =>
                {
                    using var scope = app.ApplicationServices.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    var hasher = new Argon2PasswordHasher();
                    var realm = new Realm { Name = "default" };
                    db.Realms.Add(realm);
                    db.Clients.Add(new ClientEntity
                    {
                        ClientId = clientId,
                        ClientSecretHash = hasher.Hash(clientSecret),
                        RealmId = realm.Id
                    });
                    var user = new User { Username = "alice", PasswordHash = "p" };
                    db.Users.Add(user);
                    await db.SaveChangesAsync();

                    // Seed authorization code manually
                    var code = new AuthorizationCode
                    {
                        Code = "code-" + Guid.NewGuid().ToString("N"),
                        ClientId = clientId,
                        RedirectUri = "https://cb",
                        CodeChallenge = null,
                        ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
                        UserId = user.Id,
                        Nonce = "n",
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                    };
                    db.AuthorizationCodes.Add(code);
                    await db.SaveChangesAsync();
                    app.Properties["code"] = code.Code;

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx));
                    });
                });
            });

        var host = await builder.StartAsync();
        // Simpler: open scope and fetch directly from db
        string actualCode;
        using (var scope2 = host.Services.CreateScope())
        {
            var db2 = scope2.ServiceProvider.GetRequiredService<AuthDbContext>();
            actualCode = db2.AuthorizationCodes.Single().Code;
        }
        return (host, actualCode);
    }

    [TestMethod]
    public async Task AuthorizationCode_Handled_ByStrategy()
    {
        var (host, code) = await CreateHostAndCodeAsync();
        using var hostRef = host;
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic("c1", "secret");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://cb"
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, await resp.Content.ReadAsStringAsync());
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("access_token", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("refresh_token", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("id_token", out _));
    }

    [TestMethod]
    public async Task AuthorizationCode_MissingCode_ReturnsInvalidRequest()
    {
        var (host, _) = await CreateHostAndCodeAsync();
        using var hostRef = host;
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic("c1", "secret");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = "https://cb" // missing code
        };
        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_request", doc.RootElement.GetProperty("error").GetString());
    }
}

// Minimal local DPoP validator for these tests
file sealed class TestCryptoDpopValidator : MrWhoOidc.Security.IDPoPValidator
{
    public Task<MrWhoOidc.Security.DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        => Task.FromResult(new MrWhoOidc.Security.DPoPValidationResult(true, null, null, null, null, null));
}
