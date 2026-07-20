using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.Helpers;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Comprehensive unit tests for Tenant Support Access Service and TenantAdminAuthorizationHandler.
/// Tests cover start/stop/get-info operations, support access authorization validation,
/// and an authorization matrix manifest test ensuring all admin endpoints are classified.
/// </summary>
[TestClass]
public sealed class TenantSupportAccessTests
{
    private static readonly Guid PlatformTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid PlatformAdminUserId = Guid.Parse("00000000-0000-0000-0000-0000000000A1");
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-0000000000B1");

    // -----------------------------------------------------------------------
    // 1. TenantSupportAccessService unit tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates an in-memory AuthDbContext seeded with a tenant and platform-admin role.
    /// </summary>
    private static AuthDbContext CreateSeededDb(Guid tenantId, bool tenantActive = true)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthDbContext(options);
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "test-tenant",
            Name = "Test Tenant",
            IssuerUri = "https://auth.example.com/t/test-tenant",
            Status = tenantActive ? TenantStatus.Active : TenantStatus.Suspended,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Tenants.Add(new Tenant
        {
            Id = PlatformTenantId,
            Slug = "default",
            Name = "Default Platform Tenant",
            IssuerUri = "https://auth.example.com",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Realms.Add(new Realm { Name = "platform", TenantId = PlatformTenantId });
        db.Roles.Add(new Role
        {
            Name = "platform-admin",
            RealmId = db.Realms.First().Id,
            TenantId = PlatformTenantId,
            IsActive = true
        });
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
        {
            UserId = PlatformAdminUserId,
            RoleId = db.Roles.First().Id,
            RealmId = db.Realms.First().Id,
            IsActive = true
        });
        db.SaveChanges();
        return db;
    }

    /// <summary>
    /// No-op audit sink for tests.
    /// </summary>
    private sealed class NoopAuditSink : IAuditSink
    {
        public void Emit(string eventType, object? payload) { }
        public string HashValue(string? value) => value ?? "(null)";
    }

    /// <summary>
    /// Test session that provides string convenience methods (GetString, SetString, Remove)
    /// and implements ISession for use with DefaultHttpContext.
    /// </summary>
    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new();
        public IEnumerable<string> Keys => _store.Keys;
        public string Id { get; } = Guid.NewGuid().ToString();
        public bool IsAvailable => true;
        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
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
        public string GetString(string key)
        {
            if (TryGetValue(key, out var data))
            return data.ToString();
            return null!;
        }
        public void SetString(string key, string value)
        {
            Set(key, value.ToBytes());
        }
    }

    // ---- StartSupportAccessAsync Tests ----

    [TestMethod]
    public async Task StartSupportAccessAsync_Success_WhenUserIsPlatformAdminAndTenantActive()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        // Build service provider with authorization service for platform-admin
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString()),
            new Claim(ClaimTypes.Name, "platform-admin-user")
        }, "test"));

        var started = await svc.StartSupportAccessAsync(context, user, TestTenantId,
            "Bug triage", expiryMinutes: 15);

        Assert.IsTrue(started, "StartSupportAccessAsync should succeed for platform admin with active tenant.");
        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsNotNull(sessionIdStr, "Session ID should be stored in ASP.NET session after start.");
        var sessionId = Guid.TryParse(sessionIdStr, out var parsed) ? parsed : null;
        Assert.IsNotNull(sessionId, "Stored session ID should be a valid GUID.");

        var persisted = await db.TenantSupportAccessSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        Assert.IsNotNull(persisted, "Session should exist in the database.");
        Assert.AreEqual(PlatformAdminUserId, persisted.PlatformAdminUserAccountId);
        Assert.AreEqual(TestTenantId, persisted.TenantId);
        Assert.AreEqual(SupportAccessMode.ReadOnly, persisted.Mode);
        Assert.AreEqual(SupportAccessStatus.Active, persisted.Status);
        Assert.AreEqual("Bug triage", persisted.Reason);
        Assert.IsTrue(persisted.ExpiresAt > DateTimeOffset.UtcNow, "ExpiresAt should be in the future.");
    }

    [TestMethod]
    public async Task StartSupportAccessAsync_Fails_WhenUserIsNotPlatformAdmin()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        // Build service provider with authorization service for platform-admin
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "some-other-user-id"),
            new Claim(ClaimTypes.Name, "not-platform-admin")
        }, "test"));

        var started = await svc.StartSupportAccessAsync(context, user, TestTenantId,
            "Bug triage", expiryMinutes: 15);

        Assert.IsFalse(started, "StartSupportAccessAsync should fail when user is not platform admin.");
        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsTrue(string.IsNullOrEmpty(sessionIdStr), "No session ID should be stored when start fails.");
    }

    [TestMethod]
    public async Task StartSupportAccessAsync_Fails_WhenTenantIsInactive()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: false);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        // Build service provider with authorization service for platform-admin
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString()),
            new Claim(ClaimTypes.Name, "platform-admin-user")
        }, "test"));

        var started = await svc.StartSupportAccessAsync(context, user, TestTenantId,
            "Bug triage", expiryMinutes: 15);

        Assert.IsFalse(started, "StartSupportAccessAsync should fail when target tenant is suspended.");
        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsTrue(string.IsNullOrEmpty(sessionIdStr), "No session ID should be stored when tenant is inactive.");
    }

    [TestMethod]
    public async Task StartSupportAccessAsync_Fails_WhenTenantNotFound()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        // Build service provider with authorization service for platform-admin
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString()),
            new Claim(ClaimTypes.Name, "platform-admin-user")
        }, "test"));

        var nonExistentTenantId = Guid.NewGuid();
        var started = await svc.StartSupportAccessAsync(context, user, nonExistentTenantId,
            "Bug triage", expiryMinutes: 15);

        Assert.IsFalse(started, "StartSupportAccessAsync should fail when tenant is not found.");
        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsTrue(string.IsNullOrEmpty(sessionIdStr), "No session ID should be stored when tenant is missing.");
    }

    [TestMethod]
    public async Task StartSupportAccessAsync_CreatesSessionWithCorrectFields()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        // Build service provider with authorization service for platform-admin
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString()),
            new Claim(ClaimTypes.Name, "platform-admin-user")
        }, "test"));

        var started = await svc.StartSupportAccessAsync(context, user, TestTenantId,
            "Investigating ticket #123", expiryMinutes: 30, ticketReference: "JIRA-123");

        Assert.IsTrue(started, "Start should succeed for valid inputs.");
        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        var sessionId = Guid.TryParse(sessionIdStr, out var parsed) ? parsed : null;
        Assert.IsNotNull(sessionId, "Session ID should be valid GUID.");

        var session = await db.TenantSupportAccessSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        Assert.IsNotNull(session, "Session must exist in DB.");
        Assert.AreEqual("Investigating ticket #123", session.Reason, "Reason should match input.");
        Assert.AreEqual("JIRA-123", session.TicketReference, "TicketReference should match input.");
        Assert.AreEqual(SupportAccessMode.ReadOnly, session.Mode, "Mode should default to ReadOnly.");
        Assert.AreEqual(SupportAccessStatus.Active, session.Status, "Status should be Active.");
        // Expiry should be ~30 minutes from now
        var expectedExpiry = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
        var diffSeconds = (session.ExpiresAt - expectedExpiry).TotalSeconds;
        Assert.IsTrue(diffSeconds >= -5 && diffSeconds <= 5,
            "ExpiresAt should be ~30 minutes from creation time (diff: " + diffSeconds + "s).");
    }

    // ---- StopSupportAccessAsync Tests ----

    [TestMethod]
    public async Task StopSupportAccessAsync_TransitionsSessionToEnded()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        // First start a session
        await svc.StartSupportAccessAsync(context, user, TestTenantId, "Need to investigate", expiryMinutes: 15);
        var startedSessionIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsNotNull(startedSessionIdStr);

        // Act: stop the session
        await svc.StopSupportAccessAsync(context);

        // Assert: session key is removed from ASP.NET session
        var afterStopIdStr = context.Session.GetString("SupportAccessSessionId");
        Assert.IsTrue(string.IsNullOrEmpty(afterStopIdStr), "Session ID should be cleared from ASP.NET session after stop.");

        // Verify session status transitioned to Ended in DB
        var sessionId = Guid.TryParse(startedSessionIdStr, out var parsed) ? parsed : null;
        var persisted = await db.TenantSupportAccessSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        Assert.IsNotNull(persisted, "Session should still exist in database.");
        Assert.AreEqual(SupportAccessStatus.Ended, persisted.Status, "Status should be Ended after stop.");
        Assert.IsNotNull(persisted.EndedAt, "EndedAt should be set after stop.");
    }

    [TestMethod]
    public async Task StopSupportAccessAsync_NoOp_WhenNoActiveSession()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();

        // Act: stop when no session is active
        await svc.StopSupportAccessAsync(context);

        // Assert: no error, no side effects
        Assert.IsTrue(string.IsNullOrEmpty(context.Session.GetString("SupportAccessSessionId")),
            "No session key should exist after stop with no active session.");
    }

    // ---- GetSupportAccessInfoAsync Tests ----

    [TestMethod]
    public async Task GetSupportAccessInfoAsync_ReturnsCorrectInfo_ForActiveSession()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        await svc.StartSupportAccessAsync(context, user, TestTenantId, "Debugging issue", expiryMinutes: 30);

        // Act
        var info = await svc.GetSupportAccessInfoAsync(context);

        // Assert
        Assert.IsNotNull(info, "Info should be returned for active session.");
        Assert.AreEqual(TestTenantId, info.TenantId);
        Assert.AreEqual("Test Tenant", info.TenantName);
        Assert.AreEqual("Debugging issue", info.Reason);
        Assert.AreEqual(SupportAccessStatus.Active, info.Status);
        Assert.IsTrue(info.ExpiresAt > DateTimeOffset.UtcNow, "ExpiresAt should be in the future.");
    }

    [TestMethod]
    public async Task GetSupportAccessInfoAsync_ReturnsNull_WhenNoSession()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();

        var info = await svc.GetSupportAccessInfoAsync(context);

        Assert.IsNull(info, "Info should be null when no support session is active.");
    }

    [TestMethod]
    public async Task GetSupportAccessInfoAsync_ReturnsNull_WhenSessionExpired()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var store = new TenantSupportAccessStore(db, MockTenantAccessor.CreateWithDefaultTenant(),
            NullLogger<TenantSupportAccessStore>.Instance);
        var logger = NullLogger<TenantSupportAccessService>.Instance;
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var svc = new TenantSupportAccessService(db, authService, store, logger, audit, metrics, Options.Create(new AuthOptions()));

        var context = new DefaultHttpContext { };
        context.Session = new TestSession();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        // Start with a very short expiry
        await svc.StartSupportAccessAsync(context, user, TestTenantId, "Quick check", expiryMinutes: 1);

        var sessionIdStr = context.Session.GetString("SupportAccessSessionId");
        var sessionId = Guid.TryParse(sessionIdStr, out var parsed) ? parsed : null;
        var session = db.TenantSupportAccessSessions.First(s => s.Id == sessionId);
        session.ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);

        var info = await svc.GetSupportAccessInfoAsync(context);

        Assert.IsNull(info, "Info should be null when session has expired.");
    }

    // -----------------------------------------------------------------------
    // 2. TenantAdminAuthorizationHandler authorization logic tests
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task TenantAdminAuthHandler_GrantsAccess_ToNormalAdmin_ForAllOperationKinds()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        // Seed tenant-admin realm and role for the test tenant
        db.Realms.Add(new Realm { Name = "default", TenantId = TestTenantId });
        db.Roles.Add(new Role
        {
            Name = "tenant-admin",
            RealmId = db.Realms.First(r => r.Name == "default" && r.TenantId == TestTenantId).Id,
            TenantId = TestTenantId,
            IsActive = true
        });
        var adminUserId = Guid.NewGuid();
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
        {
            UserId = adminUserId,
            RoleId = db.Roles.First(r => r.Name == "tenant-admin").Id,
            RealmId = db.Realms.First(r => r.Name == "default" && r.TenantId == TestTenantId).Id,
            IsActive = true
        });
        db.SaveChanges();

        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/list" } };
        httpAccessor.HttpContext = http;

        // Test each operation kind
        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var writeReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Write };
        var secWriteReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.SecuritySensitiveWrite };

        var contextRead = new AuthorizationHandlerContext(new[] { readReq },
            new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString())
        }, "test")), resource: null);
        await handler.HandleAsync(contextRead);
        Assert.IsTrue(contextRead.HasSucceeded,
            "Normal tenant admin should be granted Read access.");

        var contextWrite = new AuthorizationHandlerContext(new[] { writeReq },
            new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString())
        }, "test")), resource: null);
        await handler.HandleAsync(contextWrite);
        Assert.IsTrue(contextWrite.HasSucceeded,
            "Normal tenant admin should be granted Write access.");

        var contextSecWrite = new AuthorizationHandlerContext(new[] { secWriteReq },
            new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString())
        }, "test")), resource: null);
        await handler.HandleAsync(contextSecWrite);
        Assert.IsTrue(contextSecWrite.HasSucceeded,
            "Normal tenant admin should be granted SecuritySensitiveWrite access.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_GrantsRead_DeniesWrite_DuringReadOnlySupport()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/read-test" } };
        httpAccessor.HttpContext = http;

        // Seed a support access session in DB and store its ID in session
        var sessionId = Guid.NewGuid();
        var session = new TenantSupportAccessSession
        {
            Id = sessionId,
            PlatformAdminUserAccountId = PlatformAdminUserId,
            TenantId = TestTenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = "Testing support access",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1)
        };
        db.TenantSupportAccessSessions.Add(session);
        await db.SaveChangesAsync();
        http.Session.SetString("SupportAccessSessionId", sessionId.ToString());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        // Test Read - should succeed
        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var readContext = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(readContext);
        Assert.IsTrue(readContext.HasSucceeded,
            "ReadOnly support access should grant Read operations.");

        // Test Write - should be denied
        var writeReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Write };
        var writeContext = new AuthorizationHandlerContext(new[] { writeReq }, user, resource: null);
        await handler.HandleAsync(writeContext);
        Assert.IsFalse(writeContext.HasSucceeded,
            "ReadOnly support access should deny Write operations.");

        // Test SecuritySensitiveWrite - should be denied
        var secWriteReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.SecuritySensitiveWrite };
        var secWriteContext = new AuthorizationHandlerContext(new[] { secWriteReq }, user, resource: null);
        await handler.HandleAsync(secWriteContext);
        Assert.IsFalse(secWriteContext.HasSucceeded,
            "ReadOnly support access should deny SecuritySensitiveWrite operations.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_Denies_WhenSessionMissingFromDb()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/test" } };
        httpAccessor.HttpContext = http;

        // Set a session ID that doesn't exist in DB
        http.Session.SetString("SupportAccessSessionId", Guid.NewGuid().ToString());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var context = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded,
            "Should deny when support session ID is missing from DB.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_Denies_WhenActorDoesNotMatch()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/test" } };
        httpAccessor.HttpContext = http;

        // Seed a support session with PlatformAdminUserId
        var sessionId = Guid.NewGuid();
        var session = new TenantSupportAccessSession
        {
            Id = sessionId,
            PlatformAdminUserAccountId = PlatformAdminUserId,
            TenantId = TestTenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = "Testing",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1)
        };
        db.TenantSupportAccessSessions.Add(session);
        await db.SaveChangesAsync();
        http.Session.SetString("SupportAccessSessionId", sessionId.ToString());

        // User claims a different userId than the session's PlatformAdminUserAccountId
        var differentUserId = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, differentUserId.ToString())
        }, "test"));

        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var context = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded,
            "Should deny when actor user ID does not match session's PlatformAdminUserAccountId.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_Denies_WhenPlatformAdminRoleRevoked()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/test" } };
        httpAccessor.HttpContext = http;

        // Seed a support session
        var sessionId = Guid.NewGuid();
        var session = new TenantSupportAccessSession
        {
            Id = sessionId,
            PlatformAdminUserAccountId = PlatformAdminUserId,
            TenantId = TestTenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = "Testing",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1)
        };
        db.TenantSupportAccessSessions.Add(session);

        // Mark the platform-admin role assignment as inactive (simulating revocation)
        var platformAdminAssignment = db.UserRealmRoleAssignments.First(a => a.UserId == PlatformAdminUserId);
        platformAdminAssignment.IsActive = false;
        db.SaveChanges();
        http.Session.SetString("SupportAccessSessionId", sessionId.ToString());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var context = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded,
            "Should deny when platform-admin role was revoked since session start.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_Denies_WhenTenantBecameInactive()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/test" } };
        httpAccessor.HttpContext = http;

        // Seed a support session
        var sessionId = Guid.NewGuid();
        var session = new TenantSupportAccessSession
        {
            Id = sessionId,
            PlatformAdminUserAccountId = PlatformAdminUserId,
            TenantId = TestTenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = "Testing",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1)
        };
        db.TenantSupportAccessSessions.Add(session);

        // Mark the tenant as suspended (inactive)
        db.Tenants.First(t => t.Id == TestTenantId).Status = TenantStatus.Suspended;
        db.SaveChanges();
        http.Session.SetString("SupportAccessSessionId", sessionId.ToString());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var context = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded,
            "Should deny when target tenant became inactive.");
    }

    [TestMethod]
    public async Task TenantAdminAuthHandler_Denies_WhenSessionExpired()
    {
        using var db = CreateSeededDb(TestTenantId, tenantActive: true);
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(TestTenantId, "test-tenant", "Test Tenant");
        var switchingService = new MockTenantSwitchingService();
        var options = Options.Create(new TenantAdminAuthOptions
        {
            RealmName = "default",
            TenantAdminRoleName = "tenant-admin"
        });
        var httpAccessor = new HttpContextAccessor();
        var logger = NullLogger<TenantAdminAuthorizationHandler>.Instance;
        var defaultTenantContext = new TestDefaultTenantContext(PlatformTenantId);
        var store = new TenantSupportAccessStore(db, tenantAccessor,
            NullLogger<TenantSupportAccessStore>.Instance);
        var audit = new NoopAuditSink();
        var metrics = new NoopTenantSupportAccessMetrics();

        var handler = new TenantAdminAuthorizationHandler(
            db, tenantAccessor, switchingService, options, httpAccessor,
            logger, defaultTenantContext, store, audit, metrics);

        var http = new DefaultHttpContext { };
        http.Session = new TestSession();
        http.Request = new HttpRequest { Path = new HttpRequestPath { Value = "/admin/api/test" } };
        httpAccessor.HttpContext = http;

        // Seed a support session with already-expired expiry
        var sessionId = Guid.NewGuid();
        var session = new TenantSupportAccessSession
        {
            Id = sessionId,
            PlatformAdminUserAccountId = PlatformAdminUserId,
            TenantId = TestTenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = "Testing",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1) // Already expired
        };
        db.TenantSupportAccessSessions.Add(session);
        await db.SaveChangesAsync();
        http.Session.SetString("SupportAccessSessionId", sessionId.ToString());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, PlatformAdminUserId.ToString())
        }, "test"));

        var readReq = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Read };
        var context = new AuthorizationHandlerContext(new[] { readReq }, user, resource: null);
        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded,
            "Should deny when session has expired (now > ExpiresAt).");
    }

    // -----------------------------------------------------------------------
    // 3. Authorization Matrix Manifest Test
    // -----------------------------------------------------------------------

    [TestMethod, TestCategory("SafetySurface")]
    [DoNotParallelize]
    public async Task Every_AdminApiMutationEndpoint_HasOperationClassification()
    {
        /// <summary>
        /// Scan all registered endpoints under /admin/api or /t/{slug}/admin/api.
        /// For every mutation method (POST, PUT, PATCH, DELETE), assert that a
        /// TenantAdminOperationRequirement is present and its kind is Write or SecuritySensitiveWrite.
        /// Fail the test if any mutation endpoint lacks this classification.
        /// </summary>
        var factory = WebApplicationFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var violations = new List<string>();
        foreach (var e in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = e.RoutePattern.RawText ?? string.Join('/', e.RoutePattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p.ToString()))));

            // Only check endpoints under /admin/api or /t/{slug}/admin/api
            if (!pattern.Contains("/admin/api", StringComparison.OrdinalIgnoreCase))
                continue;

            var methods = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? Array.Empty<string>();
            var isMutation = methods.Any(m => m == "POST" || m == "PUT" || m == "PATCH" || m == "DELETE");

            if (!isMutation)
                continue;

            // Check for TenantAdminOperationRequirement metadata
            var hasOperationReq = e.Metadata.Any(m => m.GetType().Name.Contains("TenantAdminOperationRequirement", StringComparison.OrdinalIgnoreCase));

            if (!hasOperationReq)
            {
                violations.Add($"[{pattern}] Missing TenantAdminOperationRequirement for mutation methods: {string.Join(',', methods)}");
                continue;
            }

            // Verify the kind is Write or SecuritySensitiveWrite
            var operationReqMetadata = e.Metadata.FirstOrDefault(m => m.GetType().Name.Contains("TenantAdminOperationRequirement", StringComparison.OrdinalIgnoreCase));
            var kindProp = operationReqMetadata.GetType().GetProperty("Kind");
            var kindValue = kindProp.GetValue(operationReqMetadata);
            Assert.IsNotNull(kindValue, "TenantAdminOperationRequirement.Kind should not be null.");
            var kindName = kindValue.ToString();
            Assert.IsTrue(kindName == "Write" || kindName == "SecuritySensitiveWrite",
                $"Expected Write or SecuritySensitiveWrite for mutation endpoint {pattern}, got {kindName}.");
        }

        if (violations.Any())
        {
            Assert.Fail("Authorization Matrix Violations detected:\n" + violations.Join("\n"));
        }
    }
}

/// <summary>
/// Test implementation of IDefaultTenantContext for unit tests.
/// </summary>
internal sealed class TestDefaultTenantContext(Guid tenantId) : IDefaultTenantContext
{
    public string DefaultTenantSlug => "default";

    public Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(tenantId);
}

/// <summary>
/// Mock implementation of ITenantSwitchingService for unit tests.
/// </summary>
internal sealed class MockTenantSwitchingService : ITenantSwitchingService
{
    public Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user) =>
        Task.FromResult(new List<TenantAccessInfo>());
    public Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId) => Task.CompletedTask;
    public Guid? GetPreferredTenantId(HttpContext httpContext) => null;
    public string? GetPreferredTenantSlug(HttpContext httpContext) => null;
}

/// <summary>
/// No-op metrics implementation for Tenant Support Access (used in test mode).
/// Matches the production NoopTenantSupportAccessMetrics pattern.
/// </summary>
internal sealed class NoopTenantSupportAccessMetrics : ITenantSupportAccessMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth.noop");
    private static Counter<long> C(string name) => Meter.CreateCounter<long>(name + ".noop");
    private static Histogram<double> H(string name) => Meter.CreateHistogram<double>(name + ".noop");
    public Counter<long> TenantSupportAccessStarts { get; } = C("tenant_support_access.starts");
    public Counter<long> TenantSupportAccessStops { get; } = C("tenant_support_access.stops");
    public Counter<long> TenantSupportAccessExpirations { get; } = C("tenant_support_access.expirations");
    public Counter<long> TenantSupportAccessRevocations { get; } = C("tenant_support_access.revocations");
    public Counter<long> TenantSupportAccessWriteDenials { get; } = C("tenant_support_access.write_denials");
    public Counter<long> TenantSupportAccessValidationFailures { get; } = C("tenant_support_access.validation_failures");
    public Histogram<double> TenantSupportAccessSessionDuration { get; } = H("tenant_support_access.session_duration");
}
