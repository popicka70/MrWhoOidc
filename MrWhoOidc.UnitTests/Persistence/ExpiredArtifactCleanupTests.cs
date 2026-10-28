using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.UnitTests.Persistence;

/// <summary>
/// H6: several single-use tables had no expiry sweep, so they grew without bound. The sweep must remove
/// what can no longer be used while preserving the semantics that depend on those rows:
/// authorization-code reuse detection and the logout-targeting window.
/// </summary>
[TestClass]
public sealed class ExpiredArtifactCleanupTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [TestMethod]
    public async Task Sweep_RemovesExpiredArtifacts_AndKeepsLiveOnes()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            TenantId = tenantId, Code = "old", ClientId = "c", UserId = Guid.NewGuid(),
            ExpiresAt = now - ExpiredArtifactCleanupService.AuthorizationCodeRetention - TimeSpan.FromMinutes(1)
        });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            TenantId = tenantId, Code = "recent", ClientId = "c", UserId = Guid.NewGuid(),
            ExpiresAt = now - TimeSpan.FromMinutes(1)
        });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            TenantId = tenantId, Code = "live", ClientId = "c", UserId = Guid.NewGuid(),
            ExpiresAt = now.AddMinutes(5)
        });

        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId, DeviceCode = "expired-device", UserCode = "EXPIRED",
            ClientId = "c", ExpiresAt = now.AddMinutes(-1)
        });
        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId, DeviceCode = "live-device", UserCode = "LIVE",
            ClientId = "c", ExpiresAt = now.AddMinutes(10)
        });

        db.LogoutRedirectReferences.Add(new LogoutRedirectReference
        {
            Id = "ref-expired", ClientId = "c", RedirectUri = "https://rp.example/cb",
            ExpiresAt = now.AddMinutes(-1)
        });

        db.EmailConfirmations.Add(new EmailConfirmation
        {
            TenantId = tenantId, UserId = Guid.NewGuid(), Email = "a@example.com",
            TokenHash = "hash-expired", ExpiresAt = now.AddMinutes(-1)
        });

        db.TenantInvitations.Add(new TenantInvitation
        {
            TenantId = tenantId, Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM",
            TokenHash = "invite-expired", Status = TenantInvitationStatus.Pending,
            ExpiresAt = now.AddMinutes(-1)
        });
        db.TenantInvitations.Add(new TenantInvitation
        {
            TenantId = tenantId, Email = "b@example.com", NormalizedEmail = "B@EXAMPLE.COM",
            TokenHash = "invite-accepted", Status = TenantInvitationStatus.Accepted,
            ExpiresAt = now.AddMinutes(-1)
        });

        await db.SaveChangesAsync();

        var removed = await ExpiredArtifactCleanupService.SweepAsync(db);

        Assert.AreEqual(5, removed, "the expired authorization code, device code, redirect reference, email confirmation and pending invitation must go");
        Assert.AreEqual(0, await db.AuthorizationCodes.CountAsync(c => c.Code == "old"));
        Assert.AreEqual(1, await db.AuthorizationCodes.CountAsync(c => c.Code == "recent"),
            "a recently expired code must survive so that reuse detection still works");
        Assert.AreEqual(1, await db.AuthorizationCodes.CountAsync(c => c.Code == "live"));
        Assert.AreEqual(0, await db.DeviceCodes.CountAsync(d => d.DeviceCode == "expired-device"));
        Assert.AreEqual(1, await db.DeviceCodes.CountAsync(d => d.DeviceCode == "live-device"));
        Assert.AreEqual(0, await db.LogoutRedirectReferences.CountAsync());
        Assert.AreEqual(0, await db.EmailConfirmations.CountAsync());
        Assert.AreEqual(1, await db.TenantInvitations.CountAsync(i => i.TokenHash == "invite-accepted"),
            "an accepted invitation is history and must be kept");
    }

    [TestMethod]
    public async Task Sweep_IsCrossTenant_ButLeavesOtherTenantsLiveRowsAlone()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantA, DeviceCode = "a-expired", UserCode = "AEXP", ClientId = "c",
            ExpiresAt = now.AddMinutes(-1)
        });
        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantB, DeviceCode = "b-live", UserCode = "BLIV", ClientId = "c",
            ExpiresAt = now.AddMinutes(10)
        });

        await db.SaveChangesAsync();

        await ExpiredArtifactCleanupService.SweepAsync(db);

        Assert.AreEqual(0, await db.DeviceCodes.CountAsync(d => d.DeviceCode == "a-expired"),
            "expiry is tenant-independent, so the sweep must not stop at one tenant");
        Assert.AreEqual(1, await db.DeviceCodes.CountAsync(d => d.DeviceCode == "b-live"));
    }

    [TestMethod]
    public async Task Sweep_OnAnEmptyDatabase_RemovesNothing()
    {
        await using var db = CreateDb();

        Assert.AreEqual(0, await ExpiredArtifactCleanupService.SweepAsync(db));
    }
}
