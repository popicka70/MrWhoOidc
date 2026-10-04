using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers.Logout;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C2 of the 2026-10-04 assessment: end_session must not emit logout notifications from an
/// unverified id_token_hint, and must only notify RPs that took part in the user's session.
/// </summary>
[TestClass]
public sealed class LogoutTargetResolverTests
{
    private const string Issuer = "https://issuer.example.com";
    private static readonly Guid Victim = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static async Task<(AuthDbContext Db, LogoutTargetResolver Resolver, KeyStore Keys)> CreateAsync()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var keys = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Options.Create(new KeyRotationOptions()));
        await keys.GetActiveSigningKeyAsync(); // ensure a signing key exists
        return (db, TestLogoutTargetResolverFactory.Create(db, keys), keys);
    }

    private static async Task<string> SignedHintAsync(KeyStore keys, AuthDbContext db, Guid sub, string aud, string sid, DateTimeOffset? exp = null)
    {
        var key = await keys.GetActiveSigningKeyAsync();
        var expires = (exp ?? DateTimeOffset.UtcNow.AddMinutes(5)).UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: aud,
            claims: new[] { new Claim("sub", sub.ToString()), new Claim("sid", sid) },
            notBefore: expires.AddHours(-2),
            expires: expires,
            signingCredentials: new SigningCredentials(key, key.Alg ?? SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string UnsignedHint(Guid sub, string sid)
    {
        static string B64(string s) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(s));
        return B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64($"{{\"iss\":\"{Issuer}\",\"sub\":\"{sub}\",\"sid\":\"{sid}\"}}") + ".";
    }

    private static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());
    private static ClaimsPrincipal SignedIn(Guid userId) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "cookie"));

    [TestMethod]
    public async Task ForgedHint_WithoutSession_YieldsNoSubject()
    {
        var (db, resolver, _) = await CreateAsync();
        using var _db = db;

        var subject = await resolver.ResolveSubjectAsync(Anonymous, UnsignedHint(Victim, "s1"), Issuer, default);

        Assert.IsNull(subject, "an unsigned id_token_hint must not identify a user to log out");
    }

    [TestMethod]
    public async Task ExpiredButSignedHint_WithoutSession_IsAccepted()
    {
        var (db, resolver, keys) = await CreateAsync();
        using var _db = db;
        var hint = await SignedHintAsync(keys, db, Victim, "rp-a", "sid-a", exp: DateTimeOffset.UtcNow.AddHours(-1));

        var subject = await resolver.ResolveSubjectAsync(Anonymous, hint, Issuer, default);

        Assert.IsNotNull(subject);
        Assert.AreEqual(Victim, subject.UserId);
        Assert.AreEqual("rp-a", subject.HintClientId);
        Assert.AreEqual("sid-a", subject.HintSid);
    }

    [TestMethod]
    public async Task HintForDifferentUser_IsIgnored_InFavourOfSessionUser()
    {
        var (db, resolver, keys) = await CreateAsync();
        using var _db = db;
        var hint = await SignedHintAsync(keys, db, Victim, "rp-a", "sid-a");

        var subject = await resolver.ResolveSubjectAsync(SignedIn(Other), hint, Issuer, default);

        Assert.IsNotNull(subject);
        Assert.AreEqual(Other, subject.UserId);
        Assert.IsNull(subject.HintSid);
    }

    [TestMethod]
    public async Task Targets_OnlyParticipatingClients_SidOnlyForHintClient()
    {
        var (db, resolver, _) = await CreateAsync();
        using var _db = db;
        var tenant = Guid.NewGuid();
        db.Clients.AddRange(
            new ClientEntity { ClientId = "rp-a", TenantId = tenant, BackChannelLogoutUri = "https://a.example/bcl" },
            new ClientEntity { ClientId = "rp-b", TenantId = tenant, FrontChannelLogoutUri = "https://b.example/fcl" },
            new ClientEntity { ClientId = "rp-unrelated", TenantId = tenant, BackChannelLogoutUri = "https://c.example/bcl" });
        db.AuthorizationCodes.Add(new AuthorizationCode { TenantId = tenant, Code = "h1", ClientId = "rp-a", UserId = Victim, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        db.Tokens.Add(new Token { TenantId = tenant, TokenHash = "t1", ClientId = "rp-b", UserId = Victim, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        db.AuthorizationCodes.Add(new AuthorizationCode { TenantId = tenant, Code = "h2", ClientId = "rp-unrelated", UserId = Other, ExpiresAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var targets = await resolver.GetTargetsAsync(new LogoutSubject(Victim, "rp-a", "sid-a"), default);

        CollectionAssert.AreEquivalent(new[] { "rp-a", "rp-b" }, targets.Select(t => t.Client.ClientId).ToArray());
        Assert.AreEqual("sid-a", targets.Single(t => t.Client.ClientId == "rp-a").Sid);
        Assert.IsNull(targets.Single(t => t.Client.ClientId == "rp-b").Sid);
        Assert.IsTrue(targets.All(t => t.Sub == Victim.ToString()));
    }
}
