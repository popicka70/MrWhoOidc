using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public class TenantCredentialVerifierTests
{
    [TestMethod]
    public async Task VerifyAsync_ReturnsTrue_ForMatchingPassword()
    {
        await using var dbContext = CreateDbContext();
        var passwordHasher = new Argon2PasswordHasher();
        var password = "StrongP@ssw0rd";
        var normalizedEmail = EmailNormalizer.NormalizeForLookup("alice@example.com")!;

        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHasher.Hash(password),
            HashAlgorithm = SecurityConstants.HashAlgorithms.Argon2id
        });
        await dbContext.SaveChangesAsync();

        var verifier = new TenantCredentialVerifier(dbContext, passwordHasher, NullLogger<TenantCredentialVerifier>.Instance);

        var result = await verifier.VerifyAsync("alice@example.com", password);

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task VerifyAsync_ReturnsFalse_ForWrongPassword()
    {
        await using var dbContext = CreateDbContext();
        var passwordHasher = new Argon2PasswordHasher();
        var normalizedEmail = EmailNormalizer.NormalizeForLookup("bob@example.com")!;

        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Username = "bob",
            Email = "bob@example.com",
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHasher.Hash("CorrectHorseBatteryStaple"),
            HashAlgorithm = SecurityConstants.HashAlgorithms.Argon2id
        });
        await dbContext.SaveChangesAsync();

        var verifier = new TenantCredentialVerifier(dbContext, passwordHasher, NullLogger<TenantCredentialVerifier>.Instance);

        var result = await verifier.VerifyAsync("bob@example.com", "not-the-right-password");

        Assert.IsFalse(result.Success);
    }

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(options);
    }
}
