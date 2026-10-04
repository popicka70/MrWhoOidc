using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H2 of the 2026-10-04 post-Phase-0 review: the tenant-selection ticket was bound to the user by a
/// 32-bit email hash only, so a colliding address let one member sign in as another without a password.
/// </summary>
[TestClass]
public sealed class TenantCredentialTicketBindingTests
{
    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantContext? CurrentTenant { get; private set; }
        public void SetTenant(TenantContext context) => CurrentTenant = context;
    }

    private sealed class StubMultiTenancyOptions : IMultiTenancyOptions
    {
        public bool Enabled { get; init; } = true;
        public string DefaultTenantSlug { get; init; } = "default";
    }

    [TestMethod]
    public void EmailHash_IsFullLength_AndOnlyMatchesTheSameAddress()
    {
        var hash = TenantCredentialTicketStore.HashEmail("Victim@Corp.example ");
        var ticket = new TenantCredentialTicket("t", hash, 0, [new VerifiedTenantUser(Guid.NewGuid(), Guid.NewGuid())]);

        Assert.AreEqual(64, hash.Length, "a truncated hash can be collided offline");
        Assert.IsTrue(ticket.MatchesEmail("victim@corp.example"));
        Assert.IsFalse(ticket.MatchesEmail("attacker@evil.example"));
        Assert.IsFalse(new TenantCredentialTicket("t", hash[..8], 0, ticket.VerifiedUsers).MatchesEmail("victim@corp.example"),
            "tickets in the old truncated format must not be honoured");
    }

    [TestMethod]
    public async Task TicketVerifiedForAttacker_RedeemedWithVictimEmail_DoesNotSignIn()
    {
        var tenantId = Guid.NewGuid();
        var attackerAccountId = Guid.NewGuid();
        var victimAccountId = Guid.NewGuid();
        const string victimEmail = "victim@corp.example";

        var tenantAccessor = new StubTenantAccessor();
        tenantAccessor.SetTenant(new TenantContext { TenantId = tenantId, Slug = "acme", Name = "Acme", IssuerUri = "https://issuer/t/acme", IsMultiTenantMode = true });

        // Simulates the collision: the email check passes for the victim's address, but the ticket's
        // password was proven by the attacker's account.
        var ticket = new TenantCredentialTicket("ticket-1", TenantCredentialTicketStore.HashEmail(victimEmail), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            [new VerifiedTenantUser(tenantId, attackerAccountId)]);
        var ticketStore = new Mock<ITenantCredentialTicketStore>();
        ticketStore.Setup(s => s.GetTicket("ticket-1")).Returns(ticket);

        var globalAuth = new Mock<IGlobalAuthenticationService>();
        globalAuth.Setup(s => s.FindAccountByEmailAsync(victimEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAccount { Id = victimAccountId, Username = "victim", Email = victimEmail, PasswordHash = "h" });

        var users = new Mock<IUserService>();
        users.Setup(s => s.FindByUsernameOrEmailAsync(victimEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { TenantId = tenantId, Username = "victim", Email = victimEmail });

        var branding = new Mock<ITenantBrandingService>();
        branding.Setup(s => s.GetCurrentTenantBrandingAsync()).ReturnsAsync(new TenantBranding { TenantName = "Acme" });

        var model = new LoginModel(
            users.Object,
            globalAuth.Object,
            NullLogger<LoginModel>.Instance,
            tenantAccessor,
            new StubMultiTenancyOptions(),
            Mock.Of<ITenantSettingsService>(),
            branding.Object,
            ticketStore.Object,
            Mock.Of<ILoginContinuationStore>(),
            Mock.Of<ILoginRateLimiter>(),
            Mock.Of<IWebAuthnService>(),
            Options.Create(new WebAuthnOptions()))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            TicketId = "ticket-1",
            Email = victimEmail,
        };

        var result = await model.OnGetAsync();

        Assert.IsNotInstanceOfType<RedirectResult>(result, "the victim must not be signed in");
        users.Verify(s => s.FindByUsernameOrEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ticketStore.Verify(s => s.RemoveTicket("ticket-1"), Times.Once);
    }

    // S-L10 of the same review: login mapped the account to the tenant user by username, so a tenant user that
    // merely shares the username (but belongs to another account) received the session.
    [TestMethod]
    public async Task PasswordLogin_UsernameMatchLinkedToAnotherAccount_DoesNotSignIn()
    {
        var tenantId = Guid.NewGuid();
        var alice = new UserAccount { Id = Guid.NewGuid(), Username = "alice", Email = "alice@example.com", PasswordHash = "h", SecurityStamp = "s" };

        var tenantAccessor = new StubTenantAccessor();
        tenantAccessor.SetTenant(new TenantContext { TenantId = tenantId, Slug = "acme", Name = "Acme", IssuerUri = "https://issuer/t/acme", IsMultiTenantMode = true });

        var globalAuth = new Mock<IGlobalAuthenticationService>();
        globalAuth.Setup(s => s.AuthenticateAsync("alice", "pw", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlobalAuthenticationResult.Success(alice,
                [new UserTenantMembership { UserAccountId = alice.Id, TenantId = tenantId, Status = TenantMembershipStatus.Active }]));

        var users = new Mock<IUserService>();
        users.Setup(s => s.FindByUsernameAsync("alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { TenantId = tenantId, Username = "alice", UserAccountId = Guid.NewGuid() });

        var branding = new Mock<ITenantBrandingService>();
        branding.Setup(s => s.GetCurrentTenantBrandingAsync()).ReturnsAsync(new TenantBranding { TenantName = "Acme" });

        var model = new LoginModel(
            users.Object, globalAuth.Object, NullLogger<LoginModel>.Instance, tenantAccessor, new StubMultiTenancyOptions(),
            Mock.Of<ITenantSettingsService>(), branding.Object, Mock.Of<ITenantCredentialTicketStore>(),
            Mock.Of<ILoginContinuationStore>(), Mock.Of<ILoginRateLimiter>(), Mock.Of<IWebAuthnService>(),
            Options.Create(new WebAuthnOptions()))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Username = "alice",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result, "no session for a user that belongs to another account");
    }
}
