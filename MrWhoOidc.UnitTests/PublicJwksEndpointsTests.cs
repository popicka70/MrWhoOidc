using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using System.Linq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
// using MrWhoOidc.Auth; // not needed for these focused JWKS tests
using Microsoft.AspNetCore.Http;
using MrWhoOidc.WebAuth.Security;
using System.Net;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public class PublicJwksEndpointsTests
{
    private sealed record TestEnv(IHost Host, HttpClient Client);

    private static async Task<TestEnv> CreateAsync()
    {
        var dbName = "jwks-intg-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        // Disable validation to avoid first-run constructor validation flake when optional/late-bound services are added
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = false; o.ValidateScopes = false; });
        var services = builder.Services;
        services.AddDbContextFactory<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddMemoryCache();
        services.AddLogging();
        // Register metrics early and explicitly so PublicJwksCache constructor always resolves it deterministically
        services.AddSingleton<MrWhoOidc.WebAuth.Observability.IOidcMetrics, MrWhoOidc.WebAuth.Observability.OidcMetrics>();
        services.AddScoped<IPublicJwksCache, PublicJwksCache>();
        services.Configure<AuthOptions>(o =>
        {
            o.ExposeClientJwks = true;
            o.ExposeProviderJwks = true;
            o.ExposeAggregatedProviderJwks = true;
            o.ClientJwksCacheSeconds = 60;
            o.ProviderJwksCacheSeconds = 60;
        });

        var app = builder.Build();

        // Map minimal endpoints from WebAuth Program? We only need JWKS endpoints; Program maps them when options enabled
        // Reuse extension method wiring by invoking part of WebAuth? Simpler: add WebAuth project reference already present -> Add services by calling its DI extension if exists
        // For now assume AddMrWhoOidcAuthCore adds required handlers and Program conditional mapping occurs via building full WebAuth host in other tests.
        // We mimic only the JWKS endpoints logic inline (lightweight) if missing, but expect existing Program.cs endpoints rely on services not available here.
        // Instead of duplicating, we'll manually map needed endpoints using IPublicJwksCache if registered.
        app.MapGet("/clients/{id}/jwks", async (string id, IPublicJwksCache cache, HttpContext ctx) =>
        {
            var (etag, json) = await cache.GetClientAsync(id, ctx.RequestAborted);
            ctx.Response.Headers.ETag = etag;
            return Results.Text(json, "application/json");
        });
        app.MapGet("/providers/{name}/jwks", async (string name, IPublicJwksCache cache, HttpContext ctx) =>
        {
            var (etag, json) = await cache.GetProviderAsync(name, ctx.RequestAborted);
            if (json == "__not_found__") return Results.NotFound();
            var notModified = MrWhoOidc.WebAuth.Infrastructure.Http.EtagHelpers.SetConditionalEtag(ctx, etag);
            if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(json, "application/json");
        });
        app.MapGet("/providers/jwks", async (IPublicJwksCache cache, HttpContext ctx) =>
        {
            var (etag, json) = await cache.GetAllProvidersAsync(ctx.RequestAborted);
            var notModified = MrWhoOidc.WebAuth.Infrastructure.Http.EtagHelpers.SetConditionalEtag(ctx, etag);
            if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(json, "application/json");
        });

        await app.StartAsync();
        return new TestEnv(app, app.GetTestClient());
    }

    [TestMethod]
    public async Task Client_Jwks_Empty_When_No_Configured_Keys()
    {
        var env = await CreateAsync();
        using var scope = env.Host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.Clients.Add(new ClientEntity { ClientId = "c1", ClientName = "Test" });
        await db.SaveChangesAsync();

        var json = await env.Client.GetStringAsync("/clients/c1/jwks");
        using var doc = JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("keys", out var arr));
        Assert.AreEqual(0, arr.GetArrayLength());
    }

    [TestMethod]
    public async Task Client_Jwks_Returns_Sanitized_Key_And_Etag_Changes_On_Update()
    {
        var env = await CreateAsync();
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.Clients.Add(new ClientEntity { ClientId = "c2", ClientName = "C2", PublicJwksJson = "{\"kty\":\"RSA\",\"n\":\"x\",\"e\":\"AQAB\",\"d\":\"secret\",\"kid\":\"k1\"}" });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/clients/c2/jwks");
        var etag1 = resp1.Headers.ETag?.Tag;
        var body1 = await resp1.Content.ReadAsStringAsync();
        Assert.IsNotNull(etag1);
        Assert.DoesNotContain("\"d\"", body1);

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var c = db.Clients.First(c => c.ClientId == "c2");
            c.PublicJwksJson = "{\"keys\":[{\"kty\":\"RSA\",\"n\":\"y\",\"e\":\"AQAB\",\"kid\":\"k2\"}]}";
            await db.SaveChangesAsync();
            // Invalidate cache so next fetch recomputes JWKS + ETag
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateClient("c2");
        }
        var resp2 = await env.Client.GetAsync("/clients/c2/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreNotEqual(etag1, etag2);
    }

    [TestMethod]
    public async Task Provider_Jwks_NotFound_For_Unknown()
    {
        var env = await CreateAsync();
        var resp = await env.Client.GetAsync("/providers/unknown/jwks");
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [TestMethod]
    public async Task Provider_Jwks_Returns_Active_Signing_Key_And_Dedup()
    {
        var env = await CreateAsync();
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "up1", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "dup", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"a\",\"e\":\"AQAB\",\"kid\":\"dup\"}" });
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "dup", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"b\",\"e\":\"AQAB\",\"kid\":\"dup\"}" });
            await db.SaveChangesAsync();
        }
        var json = await env.Client.GetStringAsync("/providers/up1/jwks");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.HasCount(1, arr);
        Assert.AreEqual("dup", arr[0].GetProperty("kid").GetString());
    }

    [TestMethod]
    public async Task Aggregated_Providers_Jwks_Includes_All()
    {
        var env = await CreateAsync();
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p1 = new IdentityProvider { Name = "agg1", Enabled = true };
            var p2 = new IdentityProvider { Name = "agg2", Enabled = true };
            db.IdentityProviders.AddRange(p1, p2);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p1.Id, Kid = "k-a1", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"1\",\"e\":\"AQAB\",\"kid\":\"k-a1\"}" });
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p2.Id, Kid = "k-a2", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"2\",\"e\":\"AQAB\",\"kid\":\"k-a2\"}" });
            await db.SaveChangesAsync();
        }
        var json = await env.Client.GetStringAsync("/providers/jwks");
        using var doc = JsonDocument.Parse(json);
        var kids = doc.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetProperty("kid").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "k-a1", "k-a2" }, kids);
    }

    [TestMethod]
    public async Task Provider_Jwks_Empty_When_No_Publishable()
    {
        var env = await CreateAsync();
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "unpub", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "k-u1", Alg = "RS256", Active = true, Publishable = false, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"x\",\"e\":\"AQAB\",\"kid\":\"k-u1\"}" });
            await db.SaveChangesAsync();
        }
        var json = await env.Client.GetStringAsync("/providers/unpub/jwks");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.IsEmpty(arr);
    }

    [TestMethod]
    public async Task Provider_Jwks_Etag_Changes_When_Publishable_Key_Added()
    {
        var env = await CreateAsync();
        string etag1;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "etagp1", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/etagp1/jwks");
        etag1 = resp1.Headers.ETag?.Tag ?? ""; // empty set hash
        Assert.IsFalse(string.IsNullOrEmpty(etag1));

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var provider = db.IdentityProviders.First(p => p.Name == "etagp1");
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Kid = "k-et1", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"a\",\"e\":\"AQAB\",\"kid\":\"k-et1\"}" });
            await db.SaveChangesAsync();
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateProvider("etagp1");
        }
        var resp2 = await env.Client.GetAsync("/providers/etagp1/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreNotEqual(etag1, etag2, "ETag should change after adding a publishable key");
    }

    [TestMethod]
    public async Task Provider_Jwks_Etag_Unchanged_When_NonPublishable_Key_Added()
    {
        var env = await CreateAsync();
        string etag1;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "etagp2", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/etagp2/jwks");
        etag1 = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag1));

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var provider = db.IdentityProviders.First(p => p.Name == "etagp2");
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Kid = "k-et2", Alg = "RS256", Active = true, Publishable = false, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"b\",\"e\":\"AQAB\",\"kid\":\"k-et2\"}" });
            await db.SaveChangesAsync();
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateProvider("etagp2");
        }
        var resp2 = await env.Client.GetAsync("/providers/etagp2/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreEqual(etag1, etag2, "ETag should not change when a non-publishable key is added");
    }

    [TestMethod]
    public async Task Provider_Jwks_Etag_Unchanged_When_Duplicate_Publishable_Key_With_Same_Kid_Added()
    {
        var env = await CreateAsync();
        string etag1;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "etagp3", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "dup", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"c1\",\"e\":\"AQAB\",\"kid\":\"dup\"}" });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/etagp3/jwks");
        etag1 = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag1));

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var provider = db.IdentityProviders.First(p => p.Name == "etagp3");
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Kid = "dup", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"c2\",\"e\":\"AQAB\",\"kid\":\"dup\"}" });
            await db.SaveChangesAsync();
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateProvider("etagp3");
        }
        var resp2 = await env.Client.GetAsync("/providers/etagp3/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreEqual(etag1, etag2, "ETag should remain the same when a duplicate publishable key with same kid is added");
    }

    [TestMethod]
    public async Task Aggregated_Jwks_Etag_Changes_When_Publishable_Key_Added_To_One_Provider()
    {
        var env = await CreateAsync();
        string etag1;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.IdentityProviders.Add(new IdentityProvider { Name = "agg-et1", Enabled = true });
            db.IdentityProviders.Add(new IdentityProvider { Name = "agg-et2", Enabled = true });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/jwks");
        etag1 = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag1));

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = db.IdentityProviders.First(x => x.Name == "agg-et1");
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "k-agg1", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"z1\",\"e\":\"AQAB\",\"kid\":\"k-agg1\"}" });
            await db.SaveChangesAsync();
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateAllProviders();
        }
        var resp2 = await env.Client.GetAsync("/providers/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreNotEqual(etag1, etag2, "Aggregated ETag should change after adding a publishable key to one provider");
    }

    [TestMethod]
    public async Task Aggregated_Jwks_Etag_Unchanged_When_NonPublishable_Key_Added()
    {
        var env = await CreateAsync();
        string etag1;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.IdentityProviders.Add(new IdentityProvider { Name = "agg-et3", Enabled = true });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/jwks");
        etag1 = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag1));

        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = db.IdentityProviders.First(x => x.Name == "agg-et3");
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "k-agg3", Alg = "RS256", Active = true, Publishable = false, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"z3\",\"e\":\"AQAB\",\"kid\":\"k-agg3\"}" });
            await db.SaveChangesAsync();
            var cache = scope.ServiceProvider.GetRequiredService<IPublicJwksCache>();
            cache.InvalidateAllProviders();
        }
        var resp2 = await env.Client.GetAsync("/providers/jwks");
        var etag2 = resp2.Headers.ETag?.Tag;
        Assert.AreEqual(etag1, etag2, "Aggregated ETag should not change when only a non-publishable key is added");
    }

    [TestMethod]
    public async Task Provider_Jwks_Conditional_304_When_Etag_Matches()
    {
        var env = await CreateAsync();
        string etag;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "condp1", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "k-cond1", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"a\",\"e\":\"AQAB\",\"kid\":\"k-cond1\"}" });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/condp1/jwks");
        etag = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag));
        var req = new HttpRequestMessage(HttpMethod.Get, "/providers/condp1/jwks");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp2 = await env.Client.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.NotModified, resp2.StatusCode);
        Assert.AreEqual(etag, resp2.Headers.ETag?.Tag, "ETag header should echo original on 304");
    }

    [TestMethod]
    public async Task Aggregated_Jwks_Conditional_304_When_Etag_Matches()
    {
        var env = await CreateAsync();
        string etag;
        using (var scope = env.Host.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var p = new IdentityProvider { Name = "condagg1", Enabled = true };
            db.IdentityProviders.Add(p);
            await db.SaveChangesAsync();
            db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = p.Id, Kid = "k-agg-cond1", Alg = "RS256", Active = true, Publishable = true, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"b\",\"e\":\"AQAB\",\"kid\":\"k-agg-cond1\"}" });
            await db.SaveChangesAsync();
        }
        var resp1 = await env.Client.GetAsync("/providers/jwks");
        etag = resp1.Headers.ETag?.Tag ?? "";
        Assert.IsFalse(string.IsNullOrEmpty(etag));
        var req = new HttpRequestMessage(HttpMethod.Get, "/providers/jwks");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp2 = await env.Client.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.NotModified, resp2.StatusCode);
        Assert.AreEqual(etag, resp2.Headers.ETag?.Tag, "ETag header should echo original on 304");
    }
}
