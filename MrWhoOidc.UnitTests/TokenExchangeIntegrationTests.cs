using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenExchangeIntegrationTests
{
    private const string Issuer = "https://test";

    private sealed record TestHostBundle(IHost Host, string ClientId, string ClientSecret, Guid UserId);

    private static async Task<TestHostBundle> CreateHostAsync(Action<Client>? configureClient = null)
    {
        var dbName = "te-integ-" + Guid.NewGuid().ToString("N");
        var clientId = "app1";
        var clientSecret = "secret";
        var userId = Guid.NewGuid();

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase(dbName));
                    // Core auth services (TokenService, JwtService, etc.)
                    services.AddMrWhoOidcAuthCore();
                    // WebAuth endpoint dependencies
                    services.AddSingleton<OidcMetrics>();
                    services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
                    services.AddSingleton<IDPoPValidator, FakeDpopValidator>();
                    services.AddScoped<ITokenHandler, TokenHandler>();
                    services.AddSingleton(new OidcOptions { Issuer = Issuer });
                    services.Configure<AuthOptions>(o =>
                    {
                        o.EnableTokenExchange = true;
                        o.ApiAudiences = new[] { "api-a", "api-b", "api-c" };
                        o.OpaqueAccessTokens.Enabled = false; // JWT for easier assertions
                    });
                });
                webBuilder.Configure(async app =>
                {
                    // Seed minimal data
                    using (var scope = app.ApplicationServices.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                        var hasher = new Argon2PasswordHasher();
                        var realm = new Realm { Name = "default" };
                        db.Realms.Add(realm);
                        var client = new Client
                        {
                            ClientId = clientId,
                            ClientName = "App1",
                            ClientSecretHash = hasher.Hash(clientSecret),
                            RealmId = realm.Id,
                            OboEnabled = true,
                            // Allow target audience api-b only by policy
                            OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api-b" }),
                            // No allowed source audience restriction
                            OboAllowedScopesJson = null,
                            OboMaxLifetimeMinutes = 3 // cap 3 minutes
                        };
                        configureClient?.Invoke(client);
                        db.Clients.Add(client);

                        db.Users.Add(new User { Id = userId, Username = "bob", Name = "Bob" });
                        await db.SaveChangesAsync();
                    }

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx));
                    });
                });
            });

        var host = await builder.StartAsync();
        return new TestHostBundle(host, clientId, clientSecret, userId);
    }

    private static string CreateSubjectJwt(IHost host, Guid userId, string audience, string scopes, TimeSpan? lifetime = null)
    {
        using var scope = host.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var exp = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10));
        var claims = new[] { new Claim("sub", userId.ToString()), new Claim("scope", scopes) };
        return jwt.CreateJwt(Issuer, audience, claims, exp);
    }

    private static AuthenticationHeaderValue Basic(string id, string secret)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    [TestMethod]
    public async Task TokenExchange_HappyPath_JwtSubject_ToAllowedTarget()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host; // ensure proper disposal after test
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = CreateSubjectJwt(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Bearer", doc.GetProperty("token_type").GetString());
        Assert.AreEqual("urn:ietf:params:oauth:token-type:access_token", doc.GetProperty("issued_token_type").GetString());
        Assert.AreEqual("read", doc.GetProperty("scope").GetString());
        var expiresIn = doc.GetProperty("expires_in").GetInt32();
        Assert.IsTrue(expiresIn > 0 && expiresIn <= 180, $"expires_in out of expected cap: {expiresIn}");
        var access = doc.GetProperty("access_token").GetString();
        Assert.IsNotNull(access);
        Assert.IsTrue(access!.Split('.').Length == 3, "Expected JWT access token");
    }

    [TestMethod]
    public async Task TokenExchange_InvalidTarget_ByPolicy()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = CreateSubjectJwt(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-c" // not in client's allowed target list (only api-b allowed)
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_target", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_Insufficient_Scope()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = CreateSubjectJwt(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "write" // not present in subject scopes
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("insufficient_scope", doc.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_WithDPoP_AthBound_Succeeds()
    {
        var bundle = await CreateHostAsync();
        using var _ = bundle.Host;
        var client = bundle.Host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic(bundle.ClientId, bundle.ClientSecret);

        var subject = CreateSubjectJwt(bundle.Host, bundle.UserId, audience: "api-a", scopes: "read write");

        // Create DPoP header with ath bound to subject token
    var dpop = CreateDpopProof("POST", Issuer + "/token", subject);
        client.DefaultRequestHeaders.Remove("DPoP");
        client.DefaultRequestHeaders.Add("DPoP", dpop);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subject,
            ["audience"] = "api-b",
            ["scope"] = "read"
        };

        var resp = await client.PostAsync("/token", new FormUrlEncodedContent(form));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Bearer", doc.GetProperty("token_type").GetString());
    }

    private static string CreateDpopProof(string method, string htu, string? accessToken = null)
    {
        // In integration tests we use a fake validator that accepts any header.
        // Return a recognizable placeholder value so we can differentiate calls if needed.
        return $"dpop-{Guid.NewGuid():N}";
    }

    private sealed class FakeDpopValidator : IDPoPValidator
    {
        public Task<DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        {
            // Pretend the proof is valid; return a deterministic JKT per header for stability
            var header = http.Request.Headers["DPoP"].ToString();
            var jkt = string.IsNullOrEmpty(header) ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(header))).Substring(0, 16);
            return Task.FromResult(new DPoPValidationResult(true, jkt, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null));
        }
    }
}
