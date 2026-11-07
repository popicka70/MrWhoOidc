using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.UnitTests.Services;

/// <summary>
/// The confirmation link verifies the tenant profile, but local password login reads the global
/// <see cref="UserAccount.EmailVerified"/> flag. The two must be updated together for the same address.
/// </summary>
[TestClass]
public sealed class EmailConfirmationServiceTests
{
    private static EmailConfirmationService CreateService(AuthDbContext db)
        => new(db, Options.Create(new EmailConfirmationOptions()), NullLogger<EmailConfirmationService>.Instance);

    [TestMethod]
    public async Task ConfirmingThePrimaryEmail_VerifiesTheLinkedGlobalAccount()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        using var _ = db;

        var accountId = Guid.NewGuid();
        var account = new UserAccount
        {
            Id = accountId,
            Username = "alice",
            Email = "Alice@Example.com",
            NormalizedEmail = EmailNormalizer.NormalizeForLookup("Alice@Example.com")
        };
        var user = new User
        {
            TenantId = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            UserAccountId = accountId
        };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var created = await service.CreatePrimaryConfirmationAsync(user);
        Assert.IsTrue(created.IsSuccess, "the confirmation token is created");

        var result = await service.ConfirmAsync(created.Token);

        Assert.AreEqual(EmailConfirmationVerifyStatus.Success, result.Status);
        Assert.IsTrue((await db.Users.SingleAsync(u => u.Id == user.Id)).EmailVerified);
        var storedAccount = await db.UserAccounts.SingleAsync(a => a.Id == accountId);
        Assert.IsTrue(storedAccount.EmailVerified, "the global account is the flag that local login checks");
        Assert.IsNotNull(storedAccount.EmailVerifiedAt);
    }

    [TestMethod]
    public async Task ConfirmingThePrimaryEmail_DoesNotVerifyAnAccountWithADifferentAddress()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        using var _ = db;

        var accountId = Guid.NewGuid();
        var account = new UserAccount
        {
            Id = accountId,
            Username = "alice",
            Email = "other@example.com",
            NormalizedEmail = EmailNormalizer.NormalizeForLookup("other@example.com")
        };
        var user = new User
        {
            TenantId = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            UserAccountId = accountId
        };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var created = await service.CreatePrimaryConfirmationAsync(user);
        var result = await service.ConfirmAsync(created.Token);

        Assert.AreEqual(EmailConfirmationVerifyStatus.Success, result.Status);
        Assert.IsTrue((await db.Users.SingleAsync(u => u.Id == user.Id)).EmailVerified);
        Assert.IsFalse((await db.UserAccounts.SingleAsync(a => a.Id == accountId)).EmailVerified,
            "a confirmation for a different address must not verify the account email");
    }
}
