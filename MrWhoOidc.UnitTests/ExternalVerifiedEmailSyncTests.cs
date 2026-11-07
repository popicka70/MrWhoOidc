using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// A provider-asserted verified email is already required before external linking or auto-provisioning. The
/// local account must reflect that proof; otherwise a user who signs in successfully at Google/Microsoft is
/// still blocked from the local password flow with "email not confirmed".
/// </summary>
[TestClass]
public sealed class ExternalVerifiedEmailSyncTests
{
    private static readonly Guid DefaultTenantId =
        new("00000000-0000-0000-0000-000000000001");

    [TestMethod]
    public async Task AutoProvisioning_WithAProviderVerifiedEmail_MarksTheLocalUserVerified()
    {
        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureContext: http =>
            {
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
            },
            inMemoryDbName: "ext-verified-email-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: true);

        using (scope)
        {
            var sp = ctx.RequestServices;
            var db = sp.GetRequiredService<AuthDbContext>();
            var accounts = sp.GetRequiredService<RecordingUserAccountProvisioner>();

            var result = await sp.GetRequiredService<IExternalOidcUserProvisioner>().ProvisionOrLinkUserAsync(
                provider: "google",
                issuer: "https://accounts.google.com",
                subject: "subject-1",
                email: "newuser@example.com",
                name: "New User",
                returnUrl: "/",
                clientId: null,
                correlationId: "corr",
                correlationHandle: null,
                mappedClaims: new Dictionary<string, string>
                {
                    ["email"] = "newuser@example.com",
                    ["email_verified"] = "true"
                });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual("auto_provisioned", result.Outcome);

            var user = await db.Users.SingleAsync(u => u.Id == result.UserId!.Value);
            Assert.IsTrue(user.EmailVerified, "the tenant profile is marked verified");
            Assert.IsNotNull(user.EmailVerifiedAt);

            var ensureCall = accounts.Calls.Single();
            Assert.IsTrue(ensureCall.User.EmailVerified,
                "the account provisioner receives the verified user state and mirrors it to the global account");
        }
    }

    [TestMethod]
    public async Task AnAlreadyLinkedIdentity_WithTheSameVerifiedEmail_HealsTheLocalFlags()
    {
        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureContext: http =>
            {
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
            },
            inMemoryDbName: "ext-verified-heal-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: true);

        using (scope)
        {
            var sp = ctx.RequestServices;
            var db = sp.GetRequiredService<AuthDbContext>();
            var accountId = Guid.NewGuid();
            var user = new User
            {
                TenantId = DefaultTenantId,
                Username = "existing@example.com",
                Email = "existing@example.com",
                UserAccountId = accountId,
                EmailVerified = false
            };
            var account = new UserAccount
            {
                Id = accountId,
                Username = user.Username,
                Email = "Existing@Example.com",
                NormalizedEmail = EmailNormalizer.NormalizeForLookup(user.Email),
                EmailVerified = false
            };
            db.Users.Add(user);
            db.UserAccounts.Add(account);
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Issuer = "https://accounts.google.com",
                Subject = "subject-2",
                UserId = user.Id,
                ProviderName = "google",
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await sp.GetRequiredService<IExternalOidcUserProvisioner>().ProvisionOrLinkUserAsync(
                provider: "google",
                issuer: "https://accounts.google.com",
                subject: "subject-2",
                email: "existing@example.com",
                name: "Existing",
                returnUrl: "/",
                clientId: null,
                correlationId: "corr",
                correlationHandle: null,
                mappedClaims: new Dictionary<string, string>
                {
                    ["email"] = "existing@example.com",
                    ["email_verified"] = "true"
                });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual("linked", result.Outcome);
            Assert.IsTrue((await db.Users.SingleAsync(u => u.Id == user.Id)).EmailVerified);
            Assert.IsTrue((await db.UserAccounts.SingleAsync(a => a.Id == accountId)).EmailVerified,
                "the global account is the state that local login checks");
        }
    }

    [TestMethod]
    public async Task AnAlreadyLinkedIdentity_WithADifferentProviderEmail_DoesNotVerifyTheLocalEmail()
    {
        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureContext: http =>
            {
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
            },
            inMemoryDbName: "ext-verified-mismatch-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: true);

        using (scope)
        {
            var sp = ctx.RequestServices;
            var db = sp.GetRequiredService<AuthDbContext>();
            var accountId = Guid.NewGuid();
            var user = new User
            {
                TenantId = DefaultTenantId,
                Username = "local@example.com",
                Email = "local@example.com",
                UserAccountId = accountId,
                EmailVerified = false
            };
            var account = new UserAccount
            {
                Id = accountId,
                Username = user.Username,
                Email = user.Email,
                NormalizedEmail = EmailNormalizer.NormalizeForLookup(user.Email),
                EmailVerified = false
            };
            db.Users.Add(user);
            db.UserAccounts.Add(account);
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Issuer = "https://accounts.google.com",
                Subject = "subject-3",
                UserId = user.Id,
                ProviderName = "google",
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await sp.GetRequiredService<IExternalOidcUserProvisioner>().ProvisionOrLinkUserAsync(
                provider: "google",
                issuer: "https://accounts.google.com",
                subject: "subject-3",
                email: "other@example.com",
                name: "Local",
                returnUrl: "/",
                clientId: null,
                correlationId: "corr",
                correlationHandle: null,
                mappedClaims: new Dictionary<string, string>
                {
                    ["email"] = "other@example.com",
                    ["email_verified"] = "true"
                });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsFalse((await db.Users.SingleAsync(u => u.Id == user.Id)).EmailVerified);
            Assert.IsFalse((await db.UserAccounts.SingleAsync(a => a.Id == accountId)).EmailVerified,
                "a mutable upstream address that differs from the local email is not proof for the local address");
        }
    }
}
