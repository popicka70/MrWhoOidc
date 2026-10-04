using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: enrolment advertised algorithm=SHA256, which Google Authenticator (and others) ignore,
/// so their SHA1 codes never matched. New enrolments use SHA1; legacy enrolments (no stored algorithm) keep SHA256.
/// </summary>
[TestClass]
public sealed class TotpAlgorithmTests
{
    [TestMethod]
    public async Task NewEnrolment_UsesSha1_ForProvisioningAndVerification()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "bob", PasswordHash = "h" };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        await new UserAccountService(db).EnableMfaAsync(account.Id, TotpReplayTests.Secret);

        db.ChangeTracker.Clear();
        var stored = await db.UserAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.AreEqual("SHA1", stored.TotpAlgorithm);
        StringAssert.Contains(new TotpService().GetProvisioningUri(TotpReplayTests.Secret, "bob", "issuer", algo: TotpAlgorithms.Resolve(stored.TotpAlgorithm)), "algorithm=SHA1");

        var verifier = new MfaCodeVerifier(db, new TotpService());
        Assert.IsFalse(await verifier.VerifyTotpAsync(account.Id, TotpReplayTests.CurrentCode(algorithm: "SHA256")), "SHA256 codes must not match a SHA1 enrolment");
        Assert.IsTrue(await verifier.VerifyTotpAsync(account.Id, TotpReplayTests.CurrentCode(algorithm: "SHA1")), "Authenticator-app (SHA1) codes must match");
    }

    [TestMethod]
    public async Task LegacyEnrolment_WithoutStoredAlgorithm_KeepsSha256()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = TotpReplayTests.SeedAccount(db);
        Assert.IsNull(account.TotpAlgorithm);

        Assert.IsTrue(await new MfaCodeVerifier(db, new TotpService()).VerifyTotpAsync(account.Id, TotpReplayTests.CurrentCode(algorithm: "SHA256")));
    }

    [TestMethod]
    public void ProvisioningUri_DefaultsToSha1()
        => StringAssert.Contains(new TotpService().GetProvisioningUri(TotpReplayTests.Secret, "bob", "issuer"), "algorithm=SHA1");
}
