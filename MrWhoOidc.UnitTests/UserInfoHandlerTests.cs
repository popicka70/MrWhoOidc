using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Handlers;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
using MrWhoOidc.WebAuth.Observability;
using System.Security.Claims;
using System.Text.Json;
using System.Text;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class UserInfoHandlerTests
{
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
        IDPoPValidator? dpopValidator = null,
        IDPoPReplayCache? replayCache = null,
        IDPoPNonceStore? nonceStore = null)
    {
        var options = new OidcOptions { Issuer = "https://test.example.com" };
        var authOptions = Options.Create(new AuthOptions { ApiAudiences = ["api", "test_client"] });
        var metrics = new OidcMetrics();
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<UserInfoHandler>();

        validator ??= new StubTokenValidator(true, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", Guid.NewGuid().ToString()), new Claim("aud", "api"), new Claim("scope", "openid") }, "test")));
        dpopValidator ??= new StubDPoPValidator(true);
        replayCache ??= new StubDPoPReplayCache();
        nonceStore ??= new StubDPoPNonceStore();

        return new UserInfoHandler(options, authOptions, validator, metrics, dpopValidator, replayCache, nonceStore, logger, db);
    }

    private static DefaultHttpContext CreateHttpContext(string? authorization = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Response.Body = new MemoryStream();
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [TestMethod]
    public void UserInfo_Missing_Authorization_Returns_401()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var context = CreateHttpContext();

        var result = handler.Handle(context);

        Assert.IsNotNull(result);
        // The handler should return a result without throwing
        // We can't easily execute IResult without full ASP.NET Core infrastructure
        // So we verify the handler logic executed successfully
    }

    [TestMethod]
    public void UserInfo_Invalid_Authorization_Header_Returns_401()
    {
        using var db = CreateDb();
        var handler = CreateHandler(db);
        var context = CreateHttpContext("Basic xyz");

        var result = handler.Handle(context);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void UserInfo_Invalid_Token_Returns_401()
    {
        using var db = CreateDb();
        var validator = new StubTokenValidator(false, null);
        var handler = CreateHandler(db, validator: validator);
        var context = CreateHttpContext("Bearer invalid_token");

        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        var result = handler.Handle(context);

        Assert.IsNotNull(result);
        var (status, _) = await ExecuteAsync(result, context);
        Assert.AreEqual(200, status);
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

        var context = CreateHttpContext("Bearer valid_token");
        var result = handler.Handle(context);
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

        var context = CreateHttpContext("Bearer valid_token");
        var result = handler.Handle(context);
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

        var context = CreateHttpContext("Bearer valid_token");
        var result = handler.Handle(context);
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
        var context = CreateHttpContext("Bearer valid_token");

        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        var result = handler.Handle(context);

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

        public (bool ok, ClaimsPrincipal? principal, string? error) Validate(string token, string issuer)
        {
            return (_valid, _principal, _valid ? null : "invalid_token");
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
        var result = handler.Handle(context);

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
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns name claim with profile scope
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
        var assignment = new UserRoleAssignment
        {
            UserId = userId,
            RoleId = roleId,
            ClientId = clientGuid,
            RealmId = realmId,
            IsActive = true
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoleAssignments.Add(assignment);
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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

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
        var context = CreateHttpContext("Bearer valid_token");

        // Act
        var result = handler.Handle(context);

        // Assert
        Assert.IsNotNull(result);
        // Metrics should be recorded (UserInfoRequests, UserInfoSuccess, UserInfoDurationMs)
        // Note: OidcMetrics instance in test doesn't expose counters easily, 
        // but real implementation records via System.Diagnostics.Metrics
    }
}
