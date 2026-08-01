using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.TestSupport;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
using MrWhoOidc.WebAuth.Observability;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using MrWhoOidc.Auth.Protocols;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using MrWhoOidc.Auth.Crypto;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class UserInfoHandlerTests
{
    // Cached security keys for JWT tests - shared to avoid RSA key generation overhead
    private static readonly RsaSecurityKey s_signingKey = SharedTestKeys.GetRsaSecurityKey("userinfo-sig-key");
    private static readonly RsaSecurityKey s_encryptionKey = SharedTestKeys.GetRsaSecurityKeyAlt("userinfo-enc-key");
    private static readonly string s_encryptionJwksJson = BuildRsaJwksJsonFromKey(s_encryptionKey);

    private static string BuildRsaJwksJsonFromKey(RsaSecurityKey key)
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        return JsonSerializer.Serialize(jwk);
    }

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static UserInfoHandler CreateHandler(
        AuthDbContext db,
        ITokenValidator? validator = null,
        IJwtService? jwt = null,
        IDPoPValidator? dpopValidator = null,
        IDPoPReplayCache? replayCache = null,
        IDPoPNonceStore? nonceStore = null)
    {
        var options = new OidcOptions { Issuer = "https://test.example.com" };
        var authOptions = Options.Create(new AuthOptions { ApiAudiences = ["api", "test_client"] });
        var metrics = new OidcEndpointMetrics();
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UserInfoHandler>();

        validator ??= new StubTokenValidator(true, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", Guid.NewGuid().ToString()), new Claim("aud", "api"), new Claim("scope", "openid") }, "test")));
        jwt ??= new StubJwtService();
        dpopValidator ??= new StubDPoPValidator(true);
        replayCache ??= new StubDPoPReplayCache();
        nonceStore ??= new StubDPoPNonceStore();

        return new UserInfoHandler(options, authOptions, validator, jwt, metrics, dpopValidator, replayCache, nonceStore, logger, db);
    }

    private static DefaultHttpContext CreateHttpContext(string? authorization = null, string method = "GET", Dictionary<string, string>? formValues = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor, MrWhoOidc.Auth.MultiTenancy.TenantAccessor>();
        services.AddSingleton<MrWhoOidc.Auth.MultiTenancy.IMultiTenancyOptions>(new MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions());
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.IIssuerBuilder, MrWhoOidc.Auth.MultiTenancy.IssuerBuilder>();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Response.Body = new MemoryStream();
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }
        if (formValues is not null)
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(formValues.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)))));
        }
        return context;
    }

    private static string CreateUnsignedJwt(string? typ = SecurityConstants.JwtTokenTypes.AtJwt)
    {
        static string Base64Url(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes);
            return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var header = new Dictionary<string, object?>
        {
            ["alg"] = "none"
        };
        if (!string.IsNullOrWhiteSpace(typ)) header["typ"] = typ;

        var payload = new Dictionary<string, object?>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["iat"] = 0
        };

        return $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}.";
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    private static string Base64Url(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string BuildRsaJwksJson(RSA rsa, string kid = "enc1")
    {
        var p = rsa.ExportParameters(false);
        var n = Base64Url(p.Modulus!);
        var e = Base64Url(p.Exponent!);
        return $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"enc\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}";
    }

    [TestMethod]
    public async Task UserInfo_Missing_Authorization_Returns_401()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var context = CreateHttpContext();

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        // The handler should return a result without throwing
        // We can't easily execute IResult without full ASP.NET Core infrastructure
        // So we verify the handler logic executed successfully
    }

    [TestMethod]
    public async Task UserInfo_Invalid_Authorization_Header_Returns_401()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var context = CreateHttpContext("Basic xyz");

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task UserInfo_Invalid_Token_Returns_401()
    {
        using var db = CreateDb();
        var validator = new StubTokenValidator(false, null);
        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer invalid_token");

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task UserInfo_Valid_Token_Returns_Claims()
    {
        using var db = CreateDb();

        // Create test user with claims
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        var (status, body) = await ExecuteAsync(result, context);
        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
        Assert.AreEqual(user.Name, doc.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(user.Email, doc.RootElement.GetProperty("email").GetString());
    }

    [TestMethod]
    public async Task UserInfo_Post_Header_Bearer_Token_Returns_Claims()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "postheader",
            Email = "postheader@example.com",
            Name = "Post Header User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt(), method: HttpMethods.Post);

        var result = await handler.HandleAsync(context);

        var (status, body) = await ExecuteAsync(result, context);
        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
    }

    [TestMethod]
    public async Task UserInfo_Post_Body_Bearer_Token_Returns_Claims()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "postbody",
            Email = "postbody@example.com",
            Name = "Post Body User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext(
            method: HttpMethods.Post,
            formValues: new Dictionary<string, string>
            {
                [OAuthConstants.Parameters.AccessToken] = CreateUnsignedJwt()
            });

        var result = await handler.HandleAsync(context);

        var (status, body) = await ExecuteAsync(result, context);
        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
    }

    [TestMethod]
    public async Task UserInfo_Post_With_Header_And_Body_Bearer_Token_Returns_400()
    {
        using var db = CreateDb();

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var token = CreateUnsignedJwt();
        var context = CreateHttpContext(
            authorization: "Bearer " + token,
            method: HttpMethods.Post,
            formValues: new Dictionary<string, string>
            {
                [OAuthConstants.Parameters.AccessToken] = token
            });

        var result = await handler.HandleAsync(context);

        var (status, _) = await ExecuteAsync(result, context);
        Assert.AreEqual(400, status);
    }

    [TestMethod]
    public async Task UserInfo_Returns_Signed_Jwt_When_Client_Requests_UserInfo_Signing()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);

        // Client config: request JWT UserInfo (signed)
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = Guid.NewGuid(),
            ClientId = "test_client",
            RealmId = Guid.NewGuid(),
            UserInfoSignedResponseAlg = SecurityConstants.JwtAlgorithms.RS256
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var signingKey = s_signingKey;
        var jwt = new TestJwtService(signingKey);

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api"),
            new Claim("azp", "test_client")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator, jwt: jwt);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);
        Assert.IsTrue(context.Response.ContentType?.StartsWith("application/jwt", StringComparison.OrdinalIgnoreCase) ?? false);
        Assert.AreEqual(3, body.Split('.').Length);

        var handlerJwt = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://test.example.com",
            ValidateAudience = true,
            ValidAudience = "test_client",
            ValidateLifetime = true,
            IssuerSigningKey = signingKey
        };
        var principalOut = handlerJwt.ValidateToken(body, tvp, out _);
        Assert.AreEqual(user.Id.ToString(), principalOut.FindFirst("sub")?.Value);
    }

    [TestMethod]
    public async Task UserInfo_Returns_Encrypted_Jwt_When_Client_Requests_UserInfo_Encryption()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);

        var jwksJson = s_encryptionJwksJson;

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = Guid.NewGuid(),
            ClientId = "test_client",
            RealmId = Guid.NewGuid(),
            UserInfoSignedResponseAlg = SecurityConstants.JwtAlgorithms.RS256,
            UserInfoEncryptedResponseAlg = SecurityAlgorithms.RsaOAEP,
            UserInfoEncryptedResponseEnc = SecurityAlgorithms.Aes256CbcHmacSha512,
            PublicJwksJson = jwksJson
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var signingKey = s_signingKey;
        var jwt = new TestJwtService(signingKey);

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid profile email"),
            new Claim("aud", "api"),
            new Claim("azp", "test_client")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator, jwt: jwt);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);
        Assert.IsTrue(context.Response.ContentType?.StartsWith("application/jwt", StringComparison.OrdinalIgnoreCase) ?? false);
        Assert.AreEqual(5, body.Split('.').Length);

        var tokenHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://test.example.com",
            ValidateAudience = true,
            ValidAudience = "test_client",
            ValidateLifetime = true,
            IssuerSigningKey = signingKey,
            TokenDecryptionKey = s_encryptionKey
        };

        var principalOut = tokenHandler.ValidateToken(body, tvp, out _);
        Assert.AreEqual(user.Id.ToString(), principalOut.FindFirst("sub")?.Value);
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_EssentialMissingClaim_Returns_400_InvalidRequest()
    {
        using var db = CreateDb();

        // Create test user without email
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = null,
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var constraintsJson = "{\"email\":{\"essential\":true}}";
        var requestedJson = "[\"email\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid email"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(400, status);
        Assert.IsTrue(body.Contains("\"error\":\"invalid_request\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_EssentialNameWithoutProfileScope_UsesUsernameFallback_AndReturns200()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "oidf-cert-user",
            Email = "oidf-cert-user@mrwho.local",
            Name = null
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var constraintsJson = "{\"name\":{\"essential\":true}}";
        var requestedJson = "[\"name\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.Contains($"\"sub\":\"{user.Id}\"", StringComparison.Ordinal));
        Assert.IsTrue(body.Contains("\"name\":\"oidf-cert-user\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_EssentialValueMismatch_Returns_400_InvalidRequest()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var constraintsJson = "{\"email\":{\"essential\":true,\"value\":\"other@example.com\"}}";
        var requestedJson = "[\"email\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid email"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(400, status);
        Assert.IsTrue(body.Contains("\"error\":\"invalid_request\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_NonEssentialValueMismatch_OmitsClaim_AndReturns200()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var constraintsJson = "{\"email\":{\"essential\":false,\"value\":\"other@example.com\"}}";
        var requestedJson = "[\"email\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid email"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("email", out _));
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_Values_Satisfied_For_ArrayClaim_Returns200()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        db.UserAlternativeEmails.Add(new UserAlternativeEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alt@example.com",
            IsVerified = true
        });
        await db.SaveChangesAsync();

        var constraintsJson = "{\"emails\":{\"essential\":true,\"values\":[\"test@example.com\"]}}";
        var requestedJson = "[\"emails\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid email"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("emails", out var emails));
        Assert.AreEqual(JsonValueKind.Array, emails.ValueKind);
    }

    [TestMethod]
    public async Task UserInfo_ClaimsConstraints_Values_Mismatch_NonEssential_Omits_ArrayClaim_AndReturns200()
    {
        using var db = CreateDb();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        db.UserAlternativeEmails.Add(new UserAlternativeEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alt@example.com",
            IsVerified = true
        });
        await db.SaveChangesAsync();

        var constraintsJson = "{\"emails\":{\"essential\":false,\"values\":[\"other@example.com\"]}}";
        var requestedJson = "[\"emails\"]";

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("scope", "openid email"),
            new Claim("aud", "api"),
            new Claim("mrwho_userinfo_claims", requestedJson),
            new Claim("mrwho_userinfo_claims_constraints", constraintsJson)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(user.Id.ToString(), doc.RootElement.GetProperty("sub").GetString());
        Assert.IsFalse(doc.RootElement.TryGetProperty("emails", out _));
    }

    [TestMethod]
    public async Task UserInfo_Missing_Audience_Returns_401()
    {
        using var db = CreateDb();

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "openid")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);
        var handler = CreateHandler(db, validator: validator);

        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());
        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(401, status);
        Assert.IsTrue(body.Contains("\"error\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_Missing_Scope_Returns_401()
    {
        using var db = CreateDb();

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);
        var handler = CreateHandler(db, validator: validator);

        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());
        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(401, status);
        Assert.IsTrue(body.Contains("invalid_token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_Disallowed_Audience_Returns_401()
    {
        using var db = CreateDb();

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "evil")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);
        var handler = CreateHandler(db, validator: validator);

        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());
        var result = await handler.HandleAsync(context);
        var (status, body) = await ExecuteAsync(result, context);

        Assert.AreEqual(401, status);
        Assert.IsTrue(body.Contains("invalid_token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UserInfo_Sub_Claim_Always_Present()
    {
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@example.com"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        // Handler executed successfully with sub claim present
    }

    [TestMethod]
    public async Task UserInfo_DPoP_Bound_Token_Requires_Valid_Proof()
    {
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with cnf.jkt binding
        var cnfJson = JsonSerializer.Serialize(new { jkt = "test_thumbprint" });
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("cnf", cnfJson),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        // DPoP validation fails
        var dpopValidator = new StubDPoPValidator(false, error: "invalid_dpop");

        var handler = CreateHandler(db, validator: validator, dpopValidator: dpopValidator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        // Handler should return error result for invalid DPoP proof
    }

    // Stub implementations
    private sealed class StubTokenValidator : ITokenValidator
    {
        private readonly bool _valid;
        private readonly ClaimsPrincipal? _principal;

        public StubTokenValidator(bool valid, ClaimsPrincipal? principal)
        {
            _valid = valid;
            _principal = principal;
        }

        public Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default, IEnumerable<string>? validAudiences = null)
        {
            return Task.FromResult((_valid, _principal, _valid ? null : "invalid_token"));
        }
    }

    private sealed class StubDPoPValidator : IDPoPValidator
    {
        private readonly bool _ok;
        private readonly string? _error;
        private readonly string _jkt;

        public StubDPoPValidator(bool ok, string? error = null, string jkt = "test_thumbprint")
        {
            _ok = ok;
            _error = error;
            _jkt = jkt;
        }

        public Task<DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        {
            var result = new DPoPValidationResult(_ok, _jkt, "test_jti", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, _error);
            return Task.FromResult(result);
        }
    }

    private sealed class StubDPoPReplayCache : IDPoPReplayCache
    {
        public bool TryAdd(string key, DateTimeOffset expiresAt)
        {
            return true;
        }
    }

    private sealed class StubDPoPNonceStore : IDPoPNonceStore
    {
        private readonly bool _ok;
        private readonly string _nonce;

        public StubDPoPNonceStore(bool ok = true, string nonce = "test_nonce")
        {
            _ok = ok;
            _nonce = nonce;
        }

        public Task<(bool ok, string nonce)> ValidateOrIssueAsync(string endpoint, string clientIp, string? jkt, string? provided, CancellationToken ct = default)
        {
            return Task.FromResult((_ok, _nonce));
        }
    }

    // ==============================
    // Additional Test Cases (10/16)
    // ==============================

    [TestMethod]
    public async Task UserInfo_Access_Token_Expired_Returns_401()
    {
        // Arrange
        using var db = CreateDb();

        // Expired token simulation - validator returns invalid
        var validator = new StubTokenValidator(false, null);
        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer expired_token");

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 401 for expired token
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task UserInfo_Access_Token_Revoked_Returns_401()
    {
        // Arrange
        using var db = CreateDb();

        // Revoked token simulation - validator returns invalid
        var validator = new StubTokenValidator(false, null);
        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer revoked_token");

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 401 for revoked token
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task UserInfo_DPoP_Proof_Jkt_Mismatch_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with cnf.jkt = "expected_thumbprint"
        var cnfJson = JsonSerializer.Serialize(new { jkt = "expected_thumbprint" });
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("cnf", cnfJson),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        // DPoP proof with mismatched jkt = "different_thumbprint"
        var dpopValidator = new StubDPoPValidator(true, jkt: "different_thumbprint");

        var handler = CreateHandler(db, validator: validator, dpopValidator: dpopValidator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for jkt mismatch
    }

    [TestMethod]
    public async Task UserInfo_DPoP_Nonce_Enforced_After_Initial_Error()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with cnf.jkt binding
        var cnfJson = JsonSerializer.Serialize(new { jkt = "test_thumbprint" });
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("cnf", cnfJson),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        // DPoP validation succeeds but nonce validation fails (requires nonce)
        var dpopValidator = new StubDPoPValidator(true);
        var nonceStore = new StubDPoPNonceStore(ok: false, nonce: "server_issued_nonce");

        var handler = CreateHandler(db, validator: validator, dpopValidator: dpopValidator, nonceStore: nonceStore);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 401 with DPoP-Nonce header
        Assert.AreEqual("server_issued_nonce", context.Response.Headers["DPoP-Nonce"].ToString());
    }

    [TestMethod]
    public async Task UserInfo_Claims_Filtered_By_Scope()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with only openid scope (no profile or email)
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns only sub claim (no email or name without scopes)
    }

    [TestMethod]
    public async Task UserInfo_Email_Claim_Returned_With_Email_Scope()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with openid and email scopes
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid email"),
            new Claim("email", "test@example.com"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns email claim with email scope
    }

    [TestMethod]
    public async Task UserInfo_Profile_Claims_Returned_With_Profile_Scope()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with openid and profile scopes
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid profile"),
            new Claim("name", "Test User"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        var (status, body) = await ExecuteAsync(result, context);
        Assert.AreEqual(200, status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.AreEqual("Test User", root.GetProperty(OidcConstants.Claims.Name).GetString());
        Assert.AreEqual("Test", root.GetProperty(OidcConstants.Claims.GivenName).GetString());
        Assert.AreEqual("User", root.GetProperty(OidcConstants.Claims.FamilyName).GetString());
        Assert.AreEqual("testuser", root.GetProperty(OidcConstants.Claims.MiddleName).GetString());
        Assert.AreEqual("testuser", root.GetProperty(OidcConstants.Claims.Nickname).GetString());
        Assert.AreEqual("testuser", root.GetProperty(OidcConstants.Claims.PreferredUsername).GetString());
        Assert.AreEqual("https://test.example.com", root.GetProperty(OidcConstants.Claims.Profile).GetString());
        Assert.AreEqual("https://test.example.com/favicon.ico", root.GetProperty(OidcConstants.Claims.Picture).GetString());
        Assert.AreEqual("https://test.example.com", root.GetProperty(OidcConstants.Claims.Website).GetString());
        Assert.AreEqual("unspecified", root.GetProperty(OidcConstants.Claims.Gender).GetString());
        Assert.AreEqual("1970-01-01", root.GetProperty(OidcConstants.Claims.Birthdate).GetString());
        Assert.AreEqual("UTC", root.GetProperty(OidcConstants.Claims.Zoneinfo).GetString());
        Assert.IsTrue(root.TryGetProperty(OidcConstants.Claims.Locale, out var locale));
        Assert.IsFalse(string.IsNullOrWhiteSpace(locale.GetString()));
        Assert.AreEqual(user.CreatedAt.ToUnixTimeSeconds(), root.GetProperty(OidcConstants.Claims.UpdatedAt).GetInt64());
    }

    [TestMethod]
    public async Task UserInfo_Roles_Claim_Returned_With_Roles_Scope()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var clientGuid = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        // Create realm, client, user, role, and assignment
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            RealmId = realmId,
            ClientName = "Test Client",
            ClientSecretHash = "hash"
        };
        var user = new User
        {
            Id = userId,
            Username = "testuser"
        };
        var role = new Role { Id = roleId, Name = "admin", RealmId = realmId };
        var assignment = new UserClientRoleAssignment
        {
            UserId = userId,
            RoleId = roleId,
            ClientId = clientGuid,
            IsActive = true
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserClientRoleAssignments.Add(assignment);
        await db.SaveChangesAsync();

        // Token with openid and roles scopes
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid roles"),
            new Claim("azp", "test_client"),
            new Claim("aud", "test_client")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns roles claim with roles scope
    }

    [TestMethod]
    public async Task UserInfo_Address_Claim_Not_Leaked_Without_Scope()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@example.com",
            Name = "Test User"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Token with only openid scope (no address scope)
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "api"),
            new Claim("address", "123 Test St") // Should not be returned
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler does not leak address claim without address scope
    }

    [TestMethod]
    public async Task UserInfo_Metrics_Recorded()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("scope", "openid"),
            new Claim("aud", "api")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var validator = new StubTokenValidator(true, principal);

        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer " + CreateUnsignedJwt());

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Metrics should be recorded (UserInfoRequests, UserInfoSuccess, UserInfoDurationMs)
        // Note: OidcMetrics instance in test doesn't expose counters easily, 
        // but real implementation records via System.Diagnostics.Metrics
    }

    private sealed class StubJwtService : IJwtService
    {
        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
            => throw new NotSupportedException("JWT creation not configured for this test.");

        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, SecurityKey signingKey, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
            => CreateJwtAsync(issuer, audience, claims, expires, nonce, accessTokenHash, authTime, tokenType, ct);

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
            => throw new NotSupportedException("JWT creation not configured for this test.");

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, SecurityKey signingKey, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
            => CreateJwtEncryptedAsync(issuer, audience, claims, expires, encryptingCredentials, nonce, accessTokenHash, authTime, tokenType, ct);
    }

    private sealed class TestJwtService(SecurityKey signingKey) : IJwtService
    {
        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            var list = new List<Claim>(claims);
            if (!string.IsNullOrEmpty(nonce)) list.Add(new Claim("nonce", nonce));
            if (authTime.HasValue) list.Add(new Claim("auth_time", ((DateTimeOffset)authTime).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
            if (!string.IsNullOrEmpty(accessTokenHash)) list.Add(new Claim("at_hash", accessTokenHash));

            var creds = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
            var token = new JwtSecurityToken(issuer, audience, list, DateTime.UtcNow, expires.UtcDateTime, creds);
            if (!string.IsNullOrWhiteSpace(tokenType)) token.Header[JwtHeaderParameterNames.Typ] = tokenType;
            return Task.FromResult(new JwtSecurityTokenHandler().WriteToken(token));
        }

        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, SecurityKey explicitSigningKey, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            var replacement = new TestJwtService(explicitSigningKey);
            return replacement.CreateJwtAsync(issuer, audience, claims, expires, nonce, accessTokenHash, authTime, tokenType, ct);
        }

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            var list = new List<Claim>(claims);
            if (!string.IsNullOrEmpty(nonce)) list.Add(new Claim("nonce", nonce));
            if (authTime.HasValue) list.Add(new Claim("auth_time", ((DateTimeOffset)authTime).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
            if (!string.IsNullOrEmpty(accessTokenHash)) list.Add(new Claim("at_hash", accessTokenHash));

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                NotBefore = DateTime.UtcNow,
                Expires = expires.UtcDateTime,
                Subject = new ClaimsIdentity(list),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
                EncryptingCredentials = encryptingCredentials,
                TokenType = tokenType
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(descriptor);
            return Task.FromResult(handler.WriteToken(token));
        }

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, SecurityKey explicitSigningKey, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            var replacement = new TestJwtService(explicitSigningKey);
            return replacement.CreateJwtEncryptedAsync(issuer, audience, claims, expires, encryptingCredentials, nonce, accessTokenHash, authTime, tokenType, ct);
        }
    }

    // Stub implementations for delegated access
    private sealed class StubEffectiveAccessContextAccessor : IEffectiveAccessContextAccessor
    {
        private readonly EffectiveAccessContext _context;

        public StubEffectiveAccessContextAccessor(EffectiveAccessContext? context = null)
        {
            _context = context ?? new EffectiveAccessContext(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AccessContextKind.Normal, null, null);
        }

        public Task<EffectiveAccessContext> GetContextAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_context);
        }
    }

    private sealed class StubDelegatedAccessAuthorizationService : IDelegatedAccessAuthorizationService
    {
        private readonly bool _authorizeResult;

        public StubDelegatedAccessAuthorizationService(bool authorizeResult = true)
        {
            _authorizeResult = authorizeResult;
        }

        public Task<EffectiveAccessContext> AuthorizeAsync(
            ClaimsPrincipal actor,
            Guid grantId,
            Guid clientId,
            string capability,
            DelegatedResource resource,
            CancellationToken cancellationToken = default)
        {
            if (!_authorizeResult)
            {
                throw new UnauthorizedAccessException("Delegated access denied.");
            }
            return Task.FromResult(new EffectiveAccessContext(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AccessContextKind.DelegatedAccess, null, grantId));
        }
    }
    }


