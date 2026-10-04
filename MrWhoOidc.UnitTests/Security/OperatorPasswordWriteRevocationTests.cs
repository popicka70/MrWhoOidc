using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;
using MrWhoOidc.WebAuth.Seeding;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// C14 gap: the seed manifest and bootstrap wrote passwords straight onto UserAccount, so the SecurityStamp was not
/// rotated and live tokens survived the credential change.
/// </summary>
[TestClass]
public sealed class OperatorPasswordWriteRevocationTests
{
    private static async Task<(UserAccount Account, User User)> SeedAccountWithTokenAsync(AuthDbContext db, Guid tenantId, string username)
    {
        var account = new UserAccount { Username = username, PasswordHash = "old-hash", SecurityStamp = "stamp-1" };
        var user = new User { TenantId = tenantId, Username = username, UserAccountId = account.Id };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        db.Tokens.Add(new Token { TenantId = tenantId, UserId = user.Id, ClientId = "c", TokenHash = "t", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return (account, user);
    }

    private static SeedManifestApplier CreateApplier(AuthDbContext db, Guid tenantId, bool passwordMatches)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.CurrentTenant).Returns(new TenantContext { TenantId = tenantId, Slug = "default", Name = "Default", IssuerUri = "https://issuer/default" });
        var platformSettings = new Mock<IPlatformSettingsService>();
        platformSettings.Setup(s => s.GetSettingsAsync()).ReturnsAsync(new PlatformSettings());
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("new-hash");
        hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(passwordMatches);
        return new SeedManifestApplier(
            db,
            tenantAccessor.Object,
            Mock.Of<IMultiTenancyOptions>(),
            Mock.Of<IIssuerBuilder>(),
            Options.Create(new OidcOptions()),
            Options.Create(new SeedManifestOptions { AllowUpdates = true }),
            new ConfigurationBuilder().Build(),
            hasher.Object,
            Mock.Of<IClientStore>(),
            platformSettings.Object,
            Mock.Of<IUserAccountProvisioner>(),
            NullLogger<SeedManifestApplier>.Instance,
            new UserAccountService(db));
    }

    private static SeedManifest Manifest(string username) => new()
    {
        Tenants = [new TenantSeedDefinition { Slug = "default", Name = "Default", Users = [new UserSeedDefinition { Username = username, Password = "Seeded-Pa55word!" }] }]
    };

    [TestMethod]
    public async Task SeedManifest_ChangedPassword_RotatesStampAndRevokesTokens()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (account, user) = await SeedAccountWithTokenAsync(db, tenantId, "seeded");

        await CreateApplier(db, tenantId, passwordMatches: false).ApplyForCurrentTenantAsync(Manifest("seeded"));

        var reloaded = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.AreEqual("new-hash", reloaded.PasswordHash);
        Assert.AreNotEqual("stamp-1", reloaded.SecurityStamp);
        Assert.IsNotNull((await db.Tokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id)).RevokedAt);
    }

    [TestMethod]
    public async Task SeedManifest_UnchangedPassword_LeavesSessionsAlone()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (account, user) = await SeedAccountWithTokenAsync(db, tenantId, "seeded");

        await CreateApplier(db, tenantId, passwordMatches: true).ApplyForCurrentTenantAsync(Manifest("seeded"));

        Assert.AreEqual("stamp-1", (await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id)).SecurityStamp);
        Assert.IsNull((await db.Tokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id)).RevokedAt);
    }

    [TestMethod]
    public async Task Bootstrap_AdminPassword_RotatesStampAndRevokesTokens()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (account, user) = await SeedAccountWithTokenAsync(db, Guid.NewGuid(), "admin");

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("bootstrap-hash");
        var issuer = new Mock<IIssuerBuilder>();
        issuer.Setup(i => i.BuildIssuer(It.IsAny<string>(), It.IsAny<string>())).Returns("https://idp.example/t/default");
        var tenantAccessor = new Mock<ITenantAccessor>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bootstrap:Token"] = "boot" }).Build();

        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("idp.example");
        http.Request.Headers["X-Bootstrap-Token"] = "boot";
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"adminEmail":"admin@example.com","adminPassword":"Bootstrap-Pa55word!"}"""));

        var handler = typeof(BootstrapEndpointMappingExtensions).GetMethod("HandleBootstrapAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = handler.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(HttpContext) => (object?)http,
            var t when t == typeof(AuthDbContext) => db,
            var t when t == typeof(ITenantAccessor) => tenantAccessor.Object,
            var t when t == typeof(IIssuerBuilder) => issuer.Object,
            var t when t == typeof(IPasswordHasher) => hasher.Object,
            var t when t == typeof(IUserAccountService) => new UserAccountService(db),
            var t when t == typeof(IOptions<OidcOptions>) => Options.Create(new OidcOptions()),
            var t when t == typeof(IConfiguration) => config,
            var t when t == typeof(ILoggerFactory) => NullLoggerFactory.Instance,
            var t when t == typeof(CancellationToken) => CancellationToken.None,
            var t => ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(t))!).Object,
        }).ToArray();

        var result = await (Task<IResult>)handler.Invoke(null, args)!;

        Assert.AreEqual(200, (result as IStatusCodeHttpResult)?.StatusCode);
        var reloaded = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id);
        Assert.AreEqual("bootstrap-hash", reloaded.PasswordHash);
        Assert.AreNotEqual("stamp-1", reloaded.SecurityStamp);
        Assert.IsNotNull((await db.Tokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id)).RevokedAt);
    }
}
