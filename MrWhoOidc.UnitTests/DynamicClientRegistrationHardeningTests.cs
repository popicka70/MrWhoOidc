using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Security hardening tests for RFC 7591 / RFC 7592 dynamic client registration.
/// </summary>
public sealed partial class DynamicClientRegistrationTests
{
    private const string HardeningClientId = "dyn_hardening";
    private const string HardeningRegistrationToken = "rat_hardening-token";

    private static async Task<(HttpContext ctx, Dictionary<string, string?> body)> PostRegistrationAsync(
        AuthDbContext db, Guid tenantId, object request)
    {
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));
        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);
        return (ctx, await ParseResponseAsDict(ctx));
    }

    private static async Task<Auth.Persistence.Client> SeedDynamicClientAsync(AuthDbContext db, Guid tenantId)
    {
        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = HardeningClientId,
            ClientName = "Hardening Client",
            TenantId = tenantId,
            TokenEndpointAuthMethod = "client_secret_basic",
            AllowedLoginRedirectUrisJson = "[\"https://client.example.com/callback\"]"
        };
        db.Clients.Add(client);
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = HardeningClientId,
            TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(HardeningRegistrationToken))),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<(HttpContext ctx, Dictionary<string, string?> body)> PutConfigurationAsync(
        AuthDbContext db, Guid tenantId, object request, string token = HardeningRegistrationToken)
    {
        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);
        var ctx = CreateHttpContext(
            method: "PUT",
            path: $"/register/{HardeningClientId}",
            body: JsonSerializer.Serialize(request),
            authorizationHeader: $"Bearer {token}");
        var result = await handler.UpdateClientAsync(ctx, HardeningClientId);
        await result.ExecuteAsync(ctx);
        return (ctx, await ParseResponseAsDict(ctx));
    }

    #region Redirect URI allowlist (POST and PUT)

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,hi")]
    [DataRow("file:///etc/passwd")]
    [DataRow("myapp://callback")]
    [DataRow("https://client.example.com/callback#frag")]
    [DataRow("https://user:pass@client.example.com/callback")]
    [DataRow("http://client.example.com/callback")]
    public async Task Register_DisallowedRedirectUri_Returns400InvalidRedirectUri(string redirectUri)
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var (ctx, body) = await PostRegistrationAsync(db, tenantId, new { redirect_uris = new[] { redirectUri } });

        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual("invalid_redirect_uri", body["error"]);
    }

    [TestMethod]
    [DataRow("com.example.app:/oauth2redirect")]
    [DataRow("http://127.0.0.1:51004/callback")]
    [DataRow("http://[::1]:51004/callback")]
    public async Task Register_AllowedNativeRedirectUri_Succeeds(string redirectUri)
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var (ctx, _) = await PostRegistrationAsync(db, tenantId, new { redirect_uris = new[] { redirectUri }, application_type = "native" });

        Assert.AreEqual(201, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_DisallowedPostLogoutRedirectUri_Returns400InvalidClientMetadata()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var (ctx, body) = await PostRegistrationAsync(db, tenantId, new
        {
            redirect_uris = new[] { "https://client.example.com/callback" },
            post_logout_redirect_uris = new[] { "javascript:alert(1)" }
        });

        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual("invalid_client_metadata", body["error"]);
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("myapp://callback")]
    [DataRow("https://client.example.com/callback#frag")]
    public async Task UpdateClient_DisallowedRedirectUri_Returns400AndDoesNotPersist(string redirectUri)
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedDynamicClientAsync(db, tenantId);

        var (ctx, body) = await PutConfigurationAsync(db, tenantId, new { redirect_uris = new[] { redirectUri } });

        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual("invalid_redirect_uri", body["error"]);
        var stored = await db.Clients.AsNoTracking().SingleAsync(c => c.ClientId == HardeningClientId);
        Assert.AreEqual("[\"https://client.example.com/callback\"]", stored.AllowedLoginRedirectUrisJson);
    }

    [TestMethod]
    public async Task UpdateClient_DisallowedPostLogoutRedirectUri_Returns400InvalidClientMetadata()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedDynamicClientAsync(db, tenantId);

        var (ctx, body) = await PutConfigurationAsync(db, tenantId, new
        {
            redirect_uris = new[] { "https://client.example.com/callback" },
            post_logout_redirect_uris = new[] { "javascript:alert(1)" }
        });

        Assert.AreEqual(400, ctx.Response.StatusCode);
        Assert.AreEqual("invalid_client_metadata", body["error"]);
    }

    #endregion
}
