using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.MultiTenancy;

[TestClass]
public sealed class TenantSwitchingServiceTests
{
    [TestMethod]
    public async Task SwitchTenantAsync_ReissuesPrincipalWithTargetTenantUserId()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AuthDbContext(options);

        var tenantA = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            Slug = "default",
            IssuerUri = "/t/default",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var tenantB = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Secondary",
            Slug = "secondary",
            IssuerUri = "/t/secondary",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tenants.AddRange(tenantA, tenantB);

        const string email = "admin@mrwho.local";
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);

        var tenantAUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA.Id,
            Username = "admin-default",
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = "hash",
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var tenantBUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantB.Id,
            Username = "admin-secondary",
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = "hash",
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.AddRange(tenantAUser, tenantBUser);

        var accountId = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccount
        {
            Id = accountId,
            Username = "admin",
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = "hash",
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var resolver = new CurrentUserAccountResolver(db, NullLogger<CurrentUserAccountResolver>.Instance);
        var service = new TenantSwitchingService(db, resolver, NullLogger<TenantSwitchingService>.Instance);

        var authenticationStub = new AuthenticationStub();
        authenticationStub.AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(
            BuildPrincipal(tenantAUser.Id, accountId, email),
            new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authenticationStub);
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions { Enabled = true, DefaultTenantSlug = tenantA.Slug });
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            Session = new TestSession(),
            User = authenticationStub.AuthenticateResult.Principal!
        };

        await service.SwitchTenantAsync(httpContext, tenantB.Id);

        Assert.AreEqual(tenantB.Id.ToString(), httpContext.Session.GetString(TenantSessionKeys.PreferredTenantId));
        Assert.AreEqual(tenantB.Slug, httpContext.Session.GetString(TenantSessionKeys.PreferredTenantSlug));
        Assert.IsNotNull(authenticationStub.SignedInPrincipal, "Expected re-issued principal");
        Assert.AreEqual(tenantBUser.Id.ToString(),
            authenticationStub.SignedInPrincipal!.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            "sub should match tenant-specific user");
        Assert.AreEqual(accountId.ToString(),
            authenticationStub.SignedInPrincipal.FindFirst(UserClaimTypes.UserAccountId)?.Value,
            "UserAccountId claim should flow to new cookie");
    }

    private static ClaimsPrincipal BuildPrincipal(Guid userId, Guid accountId, string email)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "admin"),
            new(ClaimTypes.Email, email),
            new(UserClaimTypes.UserAccountId, accountId.ToString()),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("amr", "pwd"),
            new("idp", "local")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private sealed class AuthenticationStub : IAuthenticationService
    {
        public AuthenticateResult AuthenticateResult { get; set; } = AuthenticateResult.NoResult();
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }
        public AuthenticationProperties? SignedInProperties { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult);

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            SignedInProperties = properties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new();

        public IEnumerable<string> Keys => _store.Keys;
        public string Id { get; } = Guid.NewGuid().ToString();
        public bool IsAvailable => true;

        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[] value)
        {
            if (_store.TryGetValue(key, out var data))
            {
                value = data;
                return true;
            }

            value = Array.Empty<byte>();
            return false;
        }
    }
}
