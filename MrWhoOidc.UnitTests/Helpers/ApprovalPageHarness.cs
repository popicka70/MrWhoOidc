using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Pages;

namespace MrWhoOidc.UnitTests.Helpers;

/// <summary>
/// Builds the device (RFC 8628) and CIBA approval page models against an in-memory database with the collaborators
/// mocked, so the approve handlers' gates (assignment, MFA, admin policy, user binding) can be tested directly.
/// Defaults are permissive (user assigned, no admin policy, tenant MFA off); each test narrows the one it covers.
/// </summary>
internal sealed class ApprovalPageHarness : IDisposable
{
    public AuthDbContext Db { get; } = TestDataSeeder.CreateInMemoryDb();
    public Guid TenantId { get; } = Guid.NewGuid();
    public TenantSettings TenantSettings { get; } = new();
    public Mock<IAuthorizationService> Authorization { get; } = new();
    public Mock<ITenantSettingsService> Settings { get; } = new();
    public Mock<IUserClientAssignmentService> Assignments { get; } = new();
    public Mock<ICibaNotificationService> CibaNotifications { get; } = new();
    public Mock<IAuthenticationService> Authentication { get; } = new();

    public ApprovalPageHarness()
    {
        Settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(() => TenantSettings);
        SetAssigned(true);
        GrantPolicies();
    }

    /// <summary>Makes <see cref="IUserClientAssignmentService.EnsureAssignedAsync"/> accept or refuse every user.</summary>
    public void SetAssigned(bool assigned) =>
        Assignments.Setup(a => a.EnsureAssignedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(assigned ? (true, null) : (false, "User is not assigned to this application"));

    /// <summary>Only the named policies succeed; with none, every policy fails.</summary>
    public void GrantPolicies(params string[] policies)
    {
        Authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                policies.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    public User SeedUser(string username, bool accountTotp = false, bool tenantUserTotp = false)
    {
        var user = new User
        {
            TenantId = TenantId,
            Username = username,
            Email = $"{username}@example.com",
            NormalizedEmail = $"{username}@example.com",
            TotpEnabled = tenantUserTotp,
        };
        var account = new UserAccount
        {
            Id = user.Id,
            Username = username,
            Email = user.Email,
            NormalizedEmail = user.NormalizedEmail,
            PasswordHash = "hash",
            TotpEnabled = accountTotp,
            TotpSecret = accountTotp ? "JBSWY3DPEHPK3PXP" : null,
        };
        user.UserAccountId = account.Id;
        Db.Users.Add(user);
        Db.UserAccounts.Add(account);
        Db.SaveChanges();
        return user;
    }

    public Auth.Persistence.Client SeedClient(string clientId, bool isSystemClient = false)
    {
        var client = new Auth.Persistence.Client { TenantId = TenantId, ClientId = clientId, ClientName = clientId, IsSystemClient = isSystemClient };
        Db.Clients.Add(client);
        Db.SaveChanges();
        return client;
    }

    public DeviceCodeEntry SeedDeviceCode(string clientId, string userCode = "ABCDEFGH")
    {
        var entry = new DeviceCodeEntry
        {
            TenantId = TenantId,
            DeviceCode = Guid.NewGuid().ToString("N"),
            UserCode = userCode,
            ClientId = clientId,
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        Db.DeviceCodes.Add(entry);
        Db.SaveChanges();
        return entry;
    }

    /// <summary>A pending CIBA request issued for <paramref name="targetUser"/> (the hint stores the resolved user id).</summary>
    public CibaAuthenticationRequest SeedCibaRequest(string clientId, User targetUser, string? clientNotificationToken = null)
    {
        var request = new CibaAuthenticationRequest
        {
            TenantId = TenantId,
            AuthReqId = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            UserIdentifierHint = targetUser.Id.ToString(),
            HintType = "login_hint",
            ScopesJson = "[\"openid\"]",
            ClientNotificationToken = clientNotificationToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        Db.CibaAuthenticationRequests.Add(request);
        Db.SaveChanges();
        return request;
    }

    /// <summary>
    /// A full cookie session for <paramref name="user"/>. With <paramref name="mfa"/> it carries the amr/acr that
    /// /LoginTotp sets, so the approve handlers' MFA re-check is satisfied.
    /// </summary>
    public static ClaimsPrincipal SessionFor(User user, bool mfa = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.Amr, "pwd"),
        };
        if (mfa)
        {
            claims.Add(new Claim(OidcConstants.Claims.Amr, "mfa"));
            claims.Add(new Claim(OidcConstants.Claims.Acr, OidcConstants.AcrValues.Mfa));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public DeviceModel CreateDeviceModel(ClaimsPrincipal session, string userCode) =>
        new(Db, Tenant(), Authorization.Object, Settings.Object, Assignments.Object, NullLogger<DeviceModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = CreateHttpContext(session, "/device") },
            UserCode = userCode,
        };

    public CibaModel CreateCibaModel(ClaimsPrincipal session, string authReqId) =>
        new(Db, Tenant(), Options.Create(new AuthOptions { EnableCiba = true }), Settings.Object, CibaNotifications.Object,
            Assignments.Object, NullLogger<CibaModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = CreateHttpContext(session, "/ciba") },
            AuthReqId = authReqId,
        };

    /// <summary>Asserts the handler signed in the short-lived preauth cookie used to start a TOTP challenge.</summary>
    public void VerifyPreauthIssued() =>
        Authentication.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), "preauth", It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Once);

    private MockTenantAccessor Tenant() => MockTenantAccessor.CreateWithTenant(TenantId, "acme");

    private DefaultHttpContext CreateHttpContext(ClaimsPrincipal session, string path)
    {
        // The handlers re-authenticate the "Cookies" scheme (a preauth cookie alone must not approve).
        Authentication.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(session, CookieAuthenticationDefaults.AuthenticationScheme)));

        var services = new ServiceCollection();
        services.AddSingleton(Authentication.Object);
        // Real account service: MfaState.HasTotpAsync reads TOTP from the global UserAccount through it (H8).
        services.AddSingleton<IUserAccountService>(new UserAccountService(Db, NullLogger<UserAccountService>.Instance));

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = session };
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = path;
        return http;
    }

    public void Dispose() => Db.Dispose();
}
