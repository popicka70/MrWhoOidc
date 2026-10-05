using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// C16 residue (2026-10-04 assessment): an upstream IdP asserting an existing user's email must never link
/// immediately, even for a client with RequireEmailLinkConfirmation = false; the confirm flow is mandatory.
/// </summary>
[TestClass]
public sealed class ExternalEmailLinkConfirmationRequiredTests
{
    [TestMethod]
    public async Task EmailMatch_WithConfirmationDisabledOnClient_StillRequiresConfirmation()
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId,
            ClientId = "web",
            ClientName = "Web",
            RealmId = Guid.NewGuid(),
            AllowExternalIdp = true,
            AllowExternalEmailLinking = true,
            RequireEmailLinkConfirmation = false
        };
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.FindByClientIdAsync("web", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureServices: services =>
            {
                services.AddSingleton<IOptions<OidcOptions>>(Options.Create(new OidcOptions { Issuer = "https://localhost" }));
                services.AddSingleton(clientStore.Object);
            },
            useEphemeralDataProtectionProvider: true);

        using (scope)
        {
            var db = ctx.RequestServices.GetRequiredService<AuthDbContext>();
            var victim = new User { TenantId = tenantId, Username = "victim", Email = "victim@example.com", NormalizedEmail = "victim@example.com" };
            db.Users.Add(victim);
            await db.SaveChangesAsync();

            var result = await ctx.RequestServices.GetRequiredService<IExternalOidcUserProvisioner>().ProvisionOrLinkUserAsync(
                provider: "up1", issuer: "https://issuer.example.com", subject: "attacker-sub",
                email: "victim@example.com", name: "Victim", returnUrl: "/authorize?client_id=web", clientId: "web",
                correlationId: "corr", correlationHandle: null,
                mappedClaims: new Dictionary<string, string> { ["email_verified"] = "true" },
                cancellationToken: default);

            Assert.IsTrue(result.RequiresConfirmation, result.Outcome);
            Assert.AreEqual("requires_confirm", result.Outcome);
            Assert.AreEqual(victim.Id, result.ConfirmationModel!.TargetUserId);
            Assert.AreEqual(0, db.ExternalIdentities.Count(), "no external identity may be linked before confirmation");
        }
    }
}
