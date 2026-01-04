using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizationMetadataServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task PopulateMetadataAsync_Derives_Acr_From_Amr_When_Missing()
    {
        using var db = CreateDb();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var svc = new AuthorizationMetadataService(meta, db);

        var code = "code1";
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim(OidcConstants.Claims.Amr, "pwd"),
            new Claim(OidcConstants.Claims.Idp, "local")
        }, "test"));

        await svc.PopulateMetadataAsync(http, code);

        Assert.IsTrue(meta.TryGetUpstream(code, out _, out var acr, out _));
        Assert.AreEqual(OidcConstants.AcrValues.Password, acr);
    }

    [TestMethod]
    public async Task PopulateMetadataAsync_Derives_Acr_Mfa_When_Amr_Includes_Mfa()
    {
        using var db = CreateDb();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var svc = new AuthorizationMetadataService(meta, db);

        var code = "code2";
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim(OidcConstants.Claims.Amr, "pwd"),
            new Claim(OidcConstants.Claims.Amr, "mfa"),
            new Claim(OidcConstants.Claims.Idp, "local")
        }, "test"));

        await svc.PopulateMetadataAsync(http, code);

        Assert.IsTrue(meta.TryGetUpstream(code, out _, out var acr, out _));
        Assert.AreEqual(OidcConstants.AcrValues.Mfa, acr);
    }
}
