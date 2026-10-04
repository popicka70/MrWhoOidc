using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
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
        AuthDbContext db, Guid tenantId, object request, string token = HardeningRegistrationToken, IClientStore? clientStore = null)
    {
        var (handler, tenantAccessor) = CreateConfigurationHandler(db, clientStore: clientStore);
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

    #region Client deletion / update side effects

    [TestMethod]
    public async Task DeleteClient_RevokesLiveTokensAndInvalidatesClientCache()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedDynamicClientAsync(db, tenantId);

        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var refresh = new Token { TenantId = tenantId, ClientId = HardeningClientId, Type = "refresh", TokenHash = "rt", ExpiresAt = expires };
        var access = new Token { TenantId = tenantId, ClientId = HardeningClientId, Type = "access", TokenHash = "at", ExpiresAt = expires };
        var otherClient = new Token { TenantId = tenantId, ClientId = "other-client", Type = "refresh", TokenHash = "other", ExpiresAt = expires };
        db.Tokens.AddRange(refresh, access, otherClient);
        await db.SaveChangesAsync();

        var clientStore = new Mock<IClientStore>();
        var (handler, tenantAccessor) = CreateConfigurationHandler(db, clientStore: clientStore.Object);
        SetTenant(tenantAccessor, tenantId);
        var ctx = CreateHttpContext(method: "DELETE", path: $"/register/{HardeningClientId}", authorizationHeader: $"Bearer {HardeningRegistrationToken}");

        var result = await handler.DeleteClientAsync(ctx, HardeningClientId);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(204, ctx.Response.StatusCode);
        var tokens = await db.Tokens.AsNoTracking().ToDictionaryAsync(t => t.TokenHash);
        Assert.IsNotNull(tokens["rt"].RevokedAt, "refresh token of the deleted client must be revoked");
        Assert.IsNotNull(tokens["at"].RevokedAt, "access token of the deleted client must be revoked");
        Assert.IsNull(tokens["other"].RevokedAt, "tokens of other clients must be untouched");
        clientStore.Verify(s => s.InvalidateClientCacheAsync(HardeningClientId, tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateClient_InvalidatesClientCache()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedDynamicClientAsync(db, tenantId);
        var clientStore = new Mock<IClientStore>();

        var (ctx, _) = await PutConfigurationAsync(db, tenantId, new { redirect_uris = new[] { "https://client.example.com/callback" } }, clientStore: clientStore.Object);

        Assert.AreEqual(200, ctx.Response.StatusCode);
        clientStore.Verify(s => s.InvalidateClientCacheAsync(HardeningClientId, tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Registration access token rotation

    private static async Task<int> GetConfigurationStatusAsync(AuthDbContext db, Guid tenantId, string token)
    {
        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);
        var ctx = CreateHttpContext(method: "GET", path: $"/register/{HardeningClientId}", authorizationHeader: $"Bearer {token}");
        var result = await handler.GetClientAsync(ctx, HardeningClientId);
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [TestMethod]
    public async Task UpdateClient_RotatesRegistrationAccessToken()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedDynamicClientAsync(db, tenantId);

        var (ctx, body) = await PutConfigurationAsync(db, tenantId, new { redirect_uris = new[] { "https://client.example.com/callback" } });

        Assert.AreEqual(200, ctx.Response.StatusCode);
        var rotated = body.GetValueOrDefault("registration_access_token");
        Assert.IsFalse(string.IsNullOrEmpty(rotated), "PUT must return a new registration_access_token");
        Assert.AreNotEqual(HardeningRegistrationToken, rotated);
        Assert.AreEqual(401, await GetConfigurationStatusAsync(db, tenantId, HardeningRegistrationToken), "old token must be invalidated");
        Assert.AreEqual(200, await GetConfigurationStatusAsync(db, tenantId, rotated!), "rotated token must work");
    }

    #endregion
}
