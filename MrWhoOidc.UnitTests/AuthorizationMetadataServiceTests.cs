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
        var svc = new AuthorizationMetadataService(db);

        var code = "code1";
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = AuthorizationCodeHasher.Hash(code), // codes are stored hashed
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

        var row = await db.AuthorizationCodes.SingleAsync();
        Assert.AreEqual(OidcConstants.AcrValues.Password, row.UpstreamAcr);
    }

    [TestMethod]
    public async Task PopulateMetadataAsync_Derives_Acr_Mfa_When_Amr_Includes_Mfa()
    {
        using var db = CreateDb();
        var svc = new AuthorizationMetadataService(db);

        var code = "code2";
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = AuthorizationCodeHasher.Hash(code), // codes are stored hashed
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

        var row = await db.AuthorizationCodes.SingleAsync();
        Assert.AreEqual(OidcConstants.AcrValues.Mfa, row.UpstreamAcr);
    }

    // C6 (2026-10-04 assessment): login context lived in a per-process singleton, so /token on another
    // replica (or after a restart) lost sid, acr, amr, idp and mapped claims.
    [TestMethod]
    public async Task PopulateMetadataAsync_PersistsLoginContext_VisibleToAFreshContext()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        const string code = "code3";

        await using (var db = new AuthDbContext(options))
        {
            db.AuthorizationCodes.Add(new AuthorizationCode
            {
                Code = AuthorizationCodeHasher.Hash(code), UserId = Guid.NewGuid(), ClientId = "c1", RedirectUri = "https://cb",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), TenantId = Guid.NewGuid()
            });
            await db.SaveChangesAsync();

            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(OidcConstants.Claims.Amr, "pwd"),
                    new Claim(OidcConstants.Claims.Amr, "mfa"),
                    new Claim(OidcConstants.Claims.Idp, "google"),
                    new Claim(OidcConstants.Claims.Sid, "session-1"),
                    new Claim("ext_map_department", "claims")
                }, "test"))
            };
            await new AuthorizationMetadataService(db).PopulateMetadataAsync(http, code);
        }

        // A different DbContext stands in for another replica handling /token.
        await using var other = new AuthDbContext(options);
        var row = await other.AuthorizationCodes.SingleAsync();
        Assert.AreEqual("session-1", row.Sid);
        Assert.AreEqual("google", row.UpstreamIdp);
        Assert.AreEqual("pwd mfa", row.UpstreamAmr);
        StringAssert.Contains(row.MappedClaimsJson, "department");
        Assert.IsNotNull(row.AuthTime);
    }
}
