using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RevocationServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task Revoke_IsIdempotent_And_Audited()
    {
        using var db = CreateDb();
        // Seed a refresh token entry
        db.Tokens.Add(new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "refresh",
            TokenHash = Hash("rt"),
            ClientId = "c1",
            UserId = Guid.NewGuid(),
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();

        var svc = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await svc.RevokeAsync("rt", "refresh_token", "c1", "127.0.0.1");
        await svc.RevokeAsync("rt", "refresh_token", "c1", "127.0.0.1");

        // Exactly one token is revoked
        Assert.AreEqual(1, db.Tokens.Count(t => t.Type == "refresh" && t.RevokedAt != null));
        // Two audit rows added (two calls)
        Assert.AreEqual(2, db.RevocationAudits.Count());
    }

    private static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
