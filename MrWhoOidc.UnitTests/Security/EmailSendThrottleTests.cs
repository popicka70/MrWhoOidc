using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Password-reset and email-verification mails were only bounded by the global request limiter, so one address
/// could be mail-bombed. Both now enforce a per-address cooldown and an hourly cap, answering a throttled request
/// exactly like one for an unknown address.
/// </summary>
[TestClass]
public sealed class EmailSendThrottleTests
{
    private static async Task<(AuthDbContext Db, PasswordResetService Service)> CreateResetAsync()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        db.UserAccounts.Add(new UserAccount { Username = "alice", Email = "alice@example.com", NormalizedEmail = EmailNormalizer.NormalizeForLookup("alice@example.com"), PasswordHash = "h" });
        await db.SaveChangesAsync();
        var service = new PasswordResetService(db, new UserAccountService(db), Mock.Of<IPasswordHasher>(), NullLogger<PasswordResetService>.Instance);
        return (db, service);
    }

    [TestMethod]
    public async Task PasswordReset_SecondRequestWithinCooldown_LooksLikeAnUnknownEmail()
    {
        var (db, service) = await CreateResetAsync();
        using var _ = db;

        var first = await service.CreateResetTokenAsync("alice@example.com");
        var second = await service.CreateResetTokenAsync("alice@example.com");
        var unknown = await service.CreateResetTokenAsync("nobody@example.com");

        Assert.IsNotNull(first.Token);
        Assert.AreEqual(unknown, second, "a throttled request must be indistinguishable from an unknown email");
        Assert.AreEqual(1, db.PasswordResetTokens.Count());
    }

    [TestMethod]
    public async Task PasswordReset_HourlyCapApplies_EvenOutsideTheCooldown()
    {
        var (db, service) = await CreateResetAsync();
        using var _ = db;
        var accountId = db.UserAccounts.Single().Id;
        for (var i = 0; i < PasswordResetService.MaxPerHour; i++)
        {
            db.PasswordResetTokens.Add(new PasswordResetToken { UserAccountId = accountId, TokenHash = $"h{i}", IsUsed = true, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-50 + i), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        }
        await db.SaveChangesAsync();

        var result = await service.CreateResetTokenAsync("alice@example.com");

        Assert.IsNull(result.Token);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task PasswordReset_AfterCooldown_IssuesANewToken()
    {
        var (db, service) = await CreateResetAsync();
        using var _ = db;
        await service.CreateResetTokenAsync("alice@example.com");
        foreach (var token in db.PasswordResetTokens) token.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        var result = await service.CreateResetTokenAsync("alice@example.com");

        Assert.IsNotNull(result.Token);
    }

    [TestMethod]
    public async Task EmailConfirmation_ResendWithinCooldown_IsThrottled()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", Email = "alice@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new EmailConfirmationService(db, Options.Create(new EmailConfirmationOptions()), NullLogger<EmailConfirmationService>.Instance);

        var first = await service.CreatePrimaryConfirmationAsync(user);
        var second = await service.CreatePrimaryConfirmationAsync(user);

        Assert.IsTrue(first.IsSuccess);
        Assert.AreEqual(EmailConfirmationCreateStatus.Throttled, second.Status);
        Assert.IsFalse(second.IsSuccess, "the workflow sends no email for a throttled request");
    }
}
