using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: TOTP verification kept no record of the last accepted time step, so a code observed by
/// a shoulder-surfer or phishing proxy could be replayed for as long as it stayed within the ±1 step window.
/// </summary>
[TestClass]
public sealed class TotpReplayTests
{
    internal const string Secret = "JBSWY3DPEHPK3PXP";

    /// <summary>RFC 6238 code for the current time step (independent of the production implementation).</summary>
    internal static string CurrentCode(string secretBase32 = Secret, string algorithm = "SHA256", long stepOffset = 0)
    {
        var key = Base32Decode(secretBase32);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        using HMAC hmac = algorithm == "SHA1" ? new HMACSHA1(key) : new HMACSHA256(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        int bits = 0, value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    internal static UserAccount SeedAccount(AuthDbContext db, string? algorithm = null)
    {
        var account = new UserAccount { Username = "alice", PasswordHash = "h", TotpEnabled = true, TotpSecret = Secret };
        db.UserAccounts.Add(account);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return account;
    }

    [TestMethod]
    public async Task SameCode_IsAcceptedOnlyOnce_InMemory()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = SeedAccount(db);
        var verifier = new MfaCodeVerifier(db, new TotpService());
        var code = CurrentCode();

        Assert.IsTrue(await verifier.VerifyTotpAsync(account.Id, code));
        Assert.IsFalse(await verifier.VerifyTotpAsync(account.Id, code), "A replayed code must be refused");
    }

    [TestMethod]
    public async Task SameCode_IsAcceptedOnlyOnce_Relational()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = SeedAccount(db);
        var code = CurrentCode();

        // Two independent contexts (two requests) presenting the same code: only one may win.
        await using var db2 = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options);
        Assert.IsTrue(await new MfaCodeVerifier(db, new TotpService()).VerifyTotpAsync(account.Id, code));
        Assert.IsFalse(await new MfaCodeVerifier(db2, new TotpService()).VerifyTotpAsync(account.Id, code), "A replayed code must be refused");

        db.ChangeTracker.Clear();
        Assert.IsNotNull((await db.UserAccounts.SingleAsync(a => a.Id == account.Id)).TotpLastUsedStep);
    }

    [TestMethod]
    public async Task OlderStep_IsRefusedAfterANewerOneWasUsed()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = SeedAccount(db);
        var verifier = new MfaCodeVerifier(db, new TotpService());

        Assert.IsTrue(await verifier.VerifyTotpAsync(account.Id, CurrentCode(stepOffset: 1)));
        Assert.IsFalse(await verifier.VerifyTotpAsync(account.Id, CurrentCode()), "A code from an earlier step must not be accepted after a later one");
    }
}
