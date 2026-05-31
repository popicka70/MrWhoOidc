using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Data isolation tests verify that sensitive data (consents, tokens, sessions, auth codes)
/// are properly isolated between tenants with no cross-tenant leakage.
/// </summary>
[TestClass]
public class DataIsolationTests
{
    private ServiceProvider? _serviceProvider;
    private AuthDbContext? _db;
    private MockTenantAccessor? _tenantAccessor;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(opts => opts
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        // Register test hybrid cache
        services.AddSingleton<HybridCache, TestHybridCache>();

        // Register mock tenant accessor
        var mockAccessor = new MockTenantAccessor();
        services.AddSingleton<ITenantAccessor>(mockAccessor);
        _tenantAccessor = mockAccessor;

        // Register mock configuration
        var inMemorySettings = new Dictionary<string, string?>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        // Register required services
        services.AddScoped<IConsentService, ConsentService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IAuthorizationCodeService, AuthorizationCodeService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthorizationCodeMetadataStore, InMemoryAuthorizationCodeMetadataStore>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();

        // Seed tenants
        var tenant1 = new Tenant
        {
            Slug = "tenant1",
            Name = "Tenant 1",
            IssuerUri = "https://tenant1.example.com",
            Status = TenantStatus.Active
        };
        var tenant2 = new Tenant
        {
            Slug = "tenant2",
            Name = "Tenant 2",
            IssuerUri = "https://tenant2.example.com",
            Status = TenantStatus.Active
        };
        _db.Tenants.AddRange(tenant1, tenant2);
        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    private void SetTenantContext(Tenant tenant)
    {
        _tenantAccessor!.SetTenant(new TenantContext
        {
            TenantId = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            IssuerUri = tenant.IssuerUri,
            IsMultiTenantMode = true
        });
    }

    #region Consent Isolation Tests

    [TestMethod]
    public void TenantScopedEntities_HaveGlobalQueryFilters()
    {
        Type[] expectedFilteredEntities =
        [
            typeof(TenantIcon),
            typeof(UserTenantMembership),
            typeof(TenantInvitation),
            typeof(TenantDomainClaim),
            typeof(User),
            typeof(WebAuthnCredential),
            typeof(Realm),
            typeof(Role),
            typeof(MrWhoOidc.Auth.Persistence.Client),
            typeof(ClientSecret),
            typeof(ClientScope),
            typeof(ClientJwksHistory),
            typeof(ClientIdentityProvider),
            typeof(UserAlternativeEmail),
            typeof(ExternalIdentity),
            typeof(UserClientAssignment),
            typeof(UserRoleAssignment),
            typeof(UserRealmRoleAssignment),
            typeof(UserClientRoleAssignment),
            typeof(IdentityProvider),
            typeof(IdentityProviderClaimMapping),
            typeof(IdentityProviderKey),
            typeof(PairwiseSubjectIdentifier),
            typeof(EmailConfirmation),
            typeof(AuthorizationCode),
            typeof(Consent),
            typeof(Token),
            typeof(RevocationAudit),
            typeof(PushedAuthorizationRequest),
            typeof(DeviceCodeEntry),
            typeof(Registration),
            typeof(BackchannelLogoutNotification),
            typeof(LogoutRedirectReference),
            typeof(QrLoginSession),
            typeof(DynamicRegistrationToken),
            typeof(CibaAuthenticationRequest),
            typeof(ImpersonationAuditLog),
            typeof(Scope),
            typeof(SigningKey),
            typeof(AuditEvent),
            typeof(MrWhoOidc.Auth.Seeding.ConfigurationAuditLog),
            typeof(MrWhoOidc.Auth.Licensing.Entities.License),
            typeof(MrWhoOidc.Auth.Licensing.Entities.LicenseHistoryEntry),
            typeof(MrWhoOidc.Auth.Licensing.Entities.FeatureUsageMetric)
        ];

        foreach (var entityType in expectedFilteredEntities)
        {
            var modelType = _db!.Model.FindEntityType(entityType);
            Assert.IsNotNull(modelType, $"{entityType.Name} must be mapped in AuthDbContext.");
            Assert.IsTrue(modelType.GetDeclaredQueryFilters().Any(), $"{entityType.Name} must have a tenant query filter.");
        }
    }

    [TestMethod]
    public async Task GlobalQueryFilters_IsolateTenantScopedEntities()
    {
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var realm1 = new Realm { TenantId = tenant1.Id, Name = "default" };
        var realm2 = new Realm { TenantId = tenant2.Id, Name = "default" };
        var user1 = new User { TenantId = tenant1.Id, Username = "user1", Email = "user1@tenant1.test" };
        var user2 = new User { TenantId = tenant2.Id, Username = "user2", Email = "user2@tenant2.test" };
        var client1 = new MrWhoOidc.Auth.Persistence.Client { TenantId = tenant1.Id, RealmId = realm1.Id, ClientId = "client-1", ClientName = "Client 1" };
        var client2 = new MrWhoOidc.Auth.Persistence.Client { TenantId = tenant2.Id, RealmId = realm2.Id, ClientId = "client-2", ClientName = "Client 2" };

        _db.Realms.AddRange(realm1, realm2);
        _db.Users.AddRange(user1, user2);
        _db.Clients.AddRange(client1, client2);
        _db.Consents.AddRange(
            new Consent { TenantId = tenant1.Id, UserId = user1.Id, ClientId = client1.ClientId, ScopesJson = "[]" },
            new Consent { TenantId = tenant2.Id, UserId = user2.Id, ClientId = client2.ClientId, ScopesJson = "[]" });
        _db.Scopes.AddRange(
            new Scope { Name = "openid", IsGlobal = true, TenantId = null },
            new Scope { Name = "tenant1-api", TenantId = tenant1.Id },
            new Scope { Name = "tenant2-api", TenantId = tenant2.Id });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        SetTenantContext(tenant1);
        var tenant1Users = await _db.Users.AsNoTracking().Select(u => u.Username).ToListAsync();
        var tenant1Clients = await _db.Clients.AsNoTracking().Select(c => c.ClientId).ToListAsync();
        var tenant1Consents = await _db.Consents.AsNoTracking().ToListAsync();
        var tenant1Scopes = await _db.Scopes.AsNoTracking().Select(s => s.Name).OrderBy(s => s).ToListAsync();

        CollectionAssert.AreEquivalent(new[] { "user1" }, tenant1Users);
        CollectionAssert.AreEquivalent(new[] { "client-1" }, tenant1Clients);
        Assert.HasCount(1, tenant1Consents);
        CollectionAssert.AreEquivalent(new[] { "openid", "tenant1-api" }, tenant1Scopes);

        SetTenantContext(tenant2);
        var tenant2Users = await _db.Users.AsNoTracking().Select(u => u.Username).ToListAsync();
        var tenant2Clients = await _db.Clients.AsNoTracking().Select(c => c.ClientId).ToListAsync();
        var tenant2Consents = await _db.Consents.AsNoTracking().ToListAsync();
        var tenant2Scopes = await _db.Scopes.AsNoTracking().Select(s => s.Name).OrderBy(s => s).ToListAsync();

        CollectionAssert.AreEquivalent(new[] { "user2" }, tenant2Users);
        CollectionAssert.AreEquivalent(new[] { "client-2" }, tenant2Clients);
        Assert.HasCount(1, tenant2Consents);
        CollectionAssert.AreEquivalent(new[] { "openid", "tenant2-api" }, tenant2Scopes);

        var allUsers = await _db.Users.IgnoreQueryFilters().AsNoTracking().CountAsync();
        Assert.AreEqual(2, allUsers);
    }

    [TestMethod]
    public async Task SaveChanges_AssignsAndGuardsTenantScopedWrites()
    {
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        SetTenantContext(tenant1);

        var autoScopedUser = new User { Username = "auto-scoped" };
        _db.Users.Add(autoScopedUser);
        await _db.SaveChangesAsync();

        Assert.AreEqual(tenant1.Id, autoScopedUser.TenantId);

        _db.ChangeTracker.Clear();
        _db.Users.Add(new User { TenantId = tenant2.Id, Username = "cross-tenant" });

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        StringAssert.Contains(ex.Message, "different tenant");
    }

    [TestMethod]
    public async Task Consents_AreIsolatedByTenant()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);

        var consent1 = new Consent
        {
            TenantId = tenant1.Id,
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid", "profile"]"""
        };
        var consent2 = new Consent
        {
            TenantId = tenant2.Id,
            UserId = user2.Id,
            ClientId = "client1", // Same client ID, different tenant
            ScopesJson = """["openid", "email"]"""
        };
        _db.Consents.AddRange(consent1, consent2);
        await _db.SaveChangesAsync();

        // Act: Query consents for Tenant 1
        var tenant1Consents = await _db.Consents
            .Where(c => c.TenantId == tenant1.Id)
            .ToListAsync();

        // Act: Query consents for Tenant 2
        var tenant2Consents = await _db.Consents
            .Where(c => c.TenantId == tenant2.Id)
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant1Consents);
        Assert.AreEqual(user1.Id, tenant1Consents[0].UserId);
        Assert.AreEqual("""["openid", "profile"]""", tenant1Consents[0].ScopesJson);

        Assert.HasCount(1, tenant2Consents);
        Assert.AreEqual(user2.Id, tenant2Consents[0].UserId);
        Assert.AreEqual("""["openid", "email"]""", tenant2Consents[0].ScopesJson);
    }

    [TestMethod]
    public async Task ConsentService_GrantConsent_UsesTenantContext()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        var consentService = _serviceProvider!.GetRequiredService<IConsentService>();

        // Act: Grant consent in Tenant 1 context
        _tenantAccessor!.SetTenant(new TenantContext
        {
            TenantId = tenant1.Id,
            Slug = tenant1.Slug,
            Name = tenant1.Name,
            IssuerUri = tenant1.IssuerUri,
            IsMultiTenantMode = true
        });
        await consentService.GrantConsentAsync(user1.Id, "client1", ["openid", "profile"]);

        // Act: Grant consent in Tenant 2 context
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant2.Id,
            Slug = tenant2.Slug,
            Name = tenant2.Name,
            IssuerUri = tenant2.IssuerUri,
            IsMultiTenantMode = true
        });
        await consentService.GrantConsentAsync(user2.Id, "client1", ["openid", "email"]);

        // Assert: Verify tenant isolation in database
        var tenant1Consents = await _db.Consents.IgnoreQueryFilters().Where(c => c.TenantId == tenant1.Id).ToListAsync();
        var tenant2Consents = await _db.Consents.IgnoreQueryFilters().Where(c => c.TenantId == tenant2.Id).ToListAsync();

        Assert.HasCount(1, tenant1Consents);
        Assert.AreEqual(tenant1.Id, tenant1Consents[0].TenantId);
        Assert.AreEqual(user1.Id, tenant1Consents[0].UserId);

        Assert.HasCount(1, tenant2Consents);
        Assert.AreEqual(tenant2.Id, tenant2Consents[0].TenantId);
        Assert.AreEqual(user2.Id, tenant2Consents[0].UserId);
    }

    [TestMethod]
    public async Task ConsentService_HasConsent_OnlyChecksCurrentTenant()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        _db.Users.Add(user1);

        // Grant consent ONLY in Tenant 1
        var consent = new Consent
        {
            TenantId = tenant1.Id,
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid", "profile"]"""
        };
        _db.Consents.Add(consent);
        await _db.SaveChangesAsync();

        var consentService = _serviceProvider!.GetRequiredService<IConsentService>();

        // Act: Check consent in Tenant 1 context (should exist)
        SetTenantContext(tenant1);
        var hasConsentInTenant1 = await consentService.HasConsentAsync(user1.Id, "client1", ["openid", "profile"]);

        // Act: Check consent in Tenant 2 context (should NOT exist)
        SetTenantContext(tenant2);
        var hasConsentInTenant2 = await consentService.HasConsentAsync(user1.Id, "client1", ["openid", "profile"]);

        // Assert
        Assert.IsTrue(hasConsentInTenant1, "Should find consent in Tenant 1");
        Assert.IsFalse(hasConsentInTenant2, "Should NOT find consent in Tenant 2 (cross-tenant isolation)");
    }

    #endregion

    #region Refresh Token Isolation Tests

    [TestMethod]
    public async Task RefreshTokens_AreIsolatedByTenant()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);

        var token1 = new Token
        {
            TenantId = tenant1.Id,
            Type = "refresh",
            TokenHash = "hash1",
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        var token2 = new Token
        {
            TenantId = tenant2.Id,
            Type = "refresh",
            TokenHash = "hash2",
            UserId = user2.Id,
            ClientId = "client1",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        _db.Tokens.AddRange(token1, token2);
        await _db.SaveChangesAsync();

        // Act: Query tokens for each tenant
        var tenant1Tokens = await _db.Tokens
            .Where(t => t.TenantId == tenant1.Id && t.Type == "refresh")
            .ToListAsync();
        var tenant2Tokens = await _db.Tokens
            .Where(t => t.TenantId == tenant2.Id && t.Type == "refresh")
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant1Tokens);
        Assert.AreEqual(user1.Id, tenant1Tokens[0].UserId);
        Assert.AreEqual("hash1", tenant1Tokens[0].TokenHash);

        Assert.HasCount(1, tenant2Tokens);
        Assert.AreEqual(user2.Id, tenant2Tokens[0].UserId);
        Assert.AreEqual("hash2", tenant2Tokens[0].TokenHash);
    }

    [TestMethod]
    public async Task RefreshTokenService_CreateRefreshToken_UsesTenantContext()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        var tokenService = _serviceProvider!.GetRequiredService<IRefreshTokenService>();

        // Act: Create token in Tenant 1
        SetTenantContext(tenant1);
        var (token1, hash1) = await tokenService.CreateRefreshTokenAsync(
            user1.Id, "client1", ["openid", "profile"]);

        // Act: Create token in Tenant 2
        SetTenantContext(tenant2);
        var (token2, hash2) = await tokenService.CreateRefreshTokenAsync(
            user2.Id, "client1", ["openid", "email"]);

        // Assert: Verify tokens are stored with correct tenant IDs
        var tenant1Tokens = await _db.Tokens.IgnoreQueryFilters().Where(t => t.TenantId == tenant1.Id).ToListAsync();
        var tenant2Tokens = await _db.Tokens.IgnoreQueryFilters().Where(t => t.TenantId == tenant2.Id).ToListAsync();

        Assert.HasCount(1, tenant1Tokens);
        Assert.AreEqual(tenant1.Id, tenant1Tokens[0].TenantId);
        Assert.AreEqual(hash1, tenant1Tokens[0].TokenHash);
        Assert.AreEqual(user1.Id, tenant1Tokens[0].UserId);

        Assert.HasCount(1, tenant2Tokens);
        Assert.AreEqual(tenant2.Id, tenant2Tokens[0].TenantId);
        Assert.AreEqual(hash2, tenant2Tokens[0].TokenHash);
        Assert.AreEqual(user2.Id, tenant2Tokens[0].UserId);
    }

    [TestMethod]
    public async Task RefreshToken_CrossTenantLookup_Fails()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        _db.Users.Add(user1);

        var token = new Token
        {
            TenantId = tenant1.Id,
            Type = "refresh",
            TokenHash = "tenant1-token-hash",
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        _db.Tokens.Add(token);
        await _db.SaveChangesAsync();

        // Act: Try to lookup Tenant 1's token from Tenant 2 context
        var tenant1Token = await _db.Tokens
            .Where(t => t.TenantId == tenant1.Id && t.TokenHash == "tenant1-token-hash")
            .FirstOrDefaultAsync();

        var tenant2Token = await _db.Tokens
            .Where(t => t.TenantId == tenant2.Id && t.TokenHash == "tenant1-token-hash")
            .FirstOrDefaultAsync();

        // Assert
        Assert.IsNotNull(tenant1Token, "Token should be found in Tenant 1");
        Assert.IsNull(tenant2Token, "Token should NOT be found in Tenant 2 (cross-tenant isolation)");
    }

    #endregion

    #region Authorization Code Isolation Tests

    [TestMethod]
    public async Task AuthorizationCodes_AreIsolatedByTenant()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);

        var code1 = new AuthorizationCode
        {
            TenantId = tenant1.Id,
            Code = "code1",
            ClientId = "client1",
            UserId = user1.Id,
            RedirectUri = "https://tenant1.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var code2 = new AuthorizationCode
        {
            TenantId = tenant2.Id,
            Code = "code2",
            ClientId = "client1",
            UserId = user2.Id,
            RedirectUri = "https://tenant2.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _db.AuthorizationCodes.AddRange(code1, code2);
        await _db.SaveChangesAsync();

        // Act: Query codes for each tenant
        var tenant1Codes = await _db.AuthorizationCodes
            .Where(c => c.TenantId == tenant1.Id)
            .ToListAsync();
        var tenant2Codes = await _db.AuthorizationCodes
            .Where(c => c.TenantId == tenant2.Id)
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant1Codes);
        Assert.AreEqual("code1", tenant1Codes[0].Code);
        Assert.AreEqual(user1.Id, tenant1Codes[0].UserId);

        Assert.HasCount(1, tenant2Codes);
        Assert.AreEqual("code2", tenant2Codes[0].Code);
        Assert.AreEqual(user2.Id, tenant2Codes[0].UserId);
    }

    [TestMethod]
    public async Task AuthorizationCodeService_CreateCode_UsesTenantContext()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        // Act: Create codes with tenant contexts directly in entities
        var code1 = new AuthorizationCode
        {
            TenantId = tenant1.Id,
            Code = "code-tenant1",
            ClientId = "client1",
            UserId = user1.Id,
            RedirectUri = "https://tenant1.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var code2 = new AuthorizationCode
        {
            TenantId = tenant2.Id,
            Code = "code-tenant2",
            ClientId = "client1",
            UserId = user2.Id,
            RedirectUri = "https://tenant2.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _db.AuthorizationCodes.AddRange(code1, code2);
        await _db.SaveChangesAsync();

        // Assert: Verify codes are stored with correct tenant IDs
        var tenant1Codes = await _db.AuthorizationCodes.Where(c => c.TenantId == tenant1.Id).ToListAsync();
        var tenant2Codes = await _db.AuthorizationCodes.Where(c => c.TenantId == tenant2.Id).ToListAsync();

        Assert.HasCount(1, tenant1Codes);
        Assert.AreEqual(tenant1.Id, tenant1Codes[0].TenantId);
        Assert.AreEqual("code-tenant1", tenant1Codes[0].Code);

        Assert.HasCount(1, tenant2Codes);
        Assert.AreEqual(tenant2.Id, tenant2Codes[0].TenantId);
        Assert.AreEqual("code-tenant2", tenant2Codes[0].Code);
    }

    [TestMethod]
    public async Task AuthorizationCode_CrossTenantLookup_ReturnsNull()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        _db.Users.Add(user1);

        var code = new AuthorizationCode
        {
            TenantId = tenant1.Id,
            Code = "tenant1-auth-code",
            ClientId = "client1",
            UserId = user1.Id,
            RedirectUri = "https://tenant1.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _db.AuthorizationCodes.Add(code);
        await _db.SaveChangesAsync();

        // Act: Try to lookup code with correct tenant ID
        var validCode = await _db.AuthorizationCodes
            .Where(c => c.TenantId == tenant1.Id && c.Code == "tenant1-auth-code")
            .FirstOrDefaultAsync();

        // Act: Try to lookup code with wrong tenant ID
        var invalidCode = await _db.AuthorizationCodes
            .Where(c => c.TenantId == tenant2.Id && c.Code == "tenant1-auth-code")
            .FirstOrDefaultAsync();

        // Assert
        Assert.IsNotNull(validCode, "Code should be found in Tenant 1");
        Assert.IsNull(invalidCode, "Code should NOT be found in Tenant 2 (cross-tenant isolation)");
    }

    #endregion

    #region User Isolation Tests

    [TestMethod]
    public async Task Users_AreIsolatedByTenant()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1A = new User
        {
            TenantId = tenant1.Id,
            Email = "alice@tenant1.com",
            NormalizedEmail = "ALICE@TENANT1.COM",
            Username = "alice",
        };
        var user1B = new User
        {
            TenantId = tenant1.Id,
            Email = "bob@tenant1.com",
            NormalizedEmail = "BOB@TENANT1.COM",
            Username = "bob",
        };
        var user2A = new User
        {
            TenantId = tenant2.Id,
            Email = "alice@tenant2.com",
            NormalizedEmail = "ALICE@TENANT2.COM",
            Username = "alice", // Same username, different tenant
        };
        _db.Users.AddRange(user1A, user1B, user2A);
        await _db.SaveChangesAsync();

        // Act: Query users per tenant
        var tenant1Users = await _db.Users.Where(u => u.TenantId == tenant1.Id).ToListAsync();
        var tenant2Users = await _db.Users.Where(u => u.TenantId == tenant2.Id).ToListAsync();

        // Assert
        Assert.HasCount(2, tenant1Users);
        Assert.IsTrue(tenant1Users.All(u => u.TenantId == tenant1.Id));

        Assert.HasCount(1, tenant2Users);
        Assert.AreEqual("alice", tenant2Users[0].Username);
        Assert.AreEqual(tenant2.Id, tenant2Users[0].TenantId);
    }

    [TestMethod]
    public async Task Users_SameUsername_DifferentTenants_AllowedBySchema()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "admin@tenant1.com",
            NormalizedEmail = "ADMIN@TENANT1.COM",
            Username = "admin",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "admin@tenant2.com",
            NormalizedEmail = "ADMIN@TENANT2.COM",
            Username = "admin", // Same username
        };
        _db.Users.AddRange(user1, user2);

        // Act & Assert: Should not throw (unique constraint is (TenantId, Username))
        await _db.SaveChangesAsync();

        var tenant1Admin = await _db.Users.FirstAsync(u => u.TenantId == tenant1.Id && u.Username == "admin");
        var tenant2Admin = await _db.Users.FirstAsync(u => u.TenantId == tenant2.Id && u.Username == "admin");

        Assert.AreEqual(tenant1.Id, tenant1Admin.TenantId);
        Assert.AreEqual(tenant2.Id, tenant2Admin.TenantId);
        Assert.AreEqual("admin@tenant1.com", tenant1Admin.Email);
        Assert.AreEqual("admin@tenant2.com", tenant2Admin.Email);
    }

    #endregion

    #region Mixed Data Scenarios

    [TestMethod]
    public async Task MultiEntityQuery_CrossTenant_ReturnsNoData()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        _db.Users.Add(user1);

        // Add consent, token, and auth code for Tenant 1
        var consent = new Consent
        {
            TenantId = tenant1.Id,
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid"]"""
        };
        var token = new Token
        {
            TenantId = tenant1.Id,
            Type = "refresh",
            TokenHash = "hash1",
            UserId = user1.Id,
            ClientId = "client1",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        var code = new AuthorizationCode
        {
            TenantId = tenant1.Id,
            Code = "code1",
            ClientId = "client1",
            UserId = user1.Id,
            RedirectUri = "https://tenant1.com/callback",
            ScopesJson = """["openid"]""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _db.Consents.Add(consent);
        _db.Tokens.Add(token);
        _db.AuthorizationCodes.Add(code);
        await _db.SaveChangesAsync();

        // Act: Query Tenant 2 for user1's data (should be empty)
        var tenant2Consents = await _db.Consents
            .Where(c => c.TenantId == tenant2.Id && c.UserId == user1.Id)
            .ToListAsync();
        var tenant2Tokens = await _db.Tokens
            .Where(t => t.TenantId == tenant2.Id && t.UserId == user1.Id)
            .ToListAsync();
        var tenant2Codes = await _db.AuthorizationCodes
            .Where(c => c.TenantId == tenant2.Id && c.UserId == user1.Id)
            .ToListAsync();

        // Assert: No cross-tenant data leakage
        Assert.IsEmpty(tenant2Consents, "No consents should exist in Tenant 2 for Tenant 1 user");
        Assert.IsEmpty(tenant2Tokens, "No tokens should exist in Tenant 2 for Tenant 1 user");
        Assert.IsEmpty(tenant2Codes, "No auth codes should exist in Tenant 2 for Tenant 1 user");
    }

    [TestMethod]
    public async Task TenantDataDeletion_DoesNotAffectOtherTenants()
    {
        // Arrange
        var tenant1 = await _db!.Tenants.FirstAsync(t => t.Slug == "tenant1");
        var tenant2 = await _db.Tenants.FirstAsync(t => t.Slug == "tenant2");

        var user1 = new User
        {
            TenantId = tenant1.Id,
            Email = "user1@tenant1.com",
            NormalizedEmail = "USER1@TENANT1.COM",
            Username = "user1",
        };
        var user2 = new User
        {
            TenantId = tenant2.Id,
            Email = "user2@tenant2.com",
            NormalizedEmail = "USER2@TENANT2.COM",
            Username = "user2",
        };
        _db.Users.AddRange(user1, user2);

        var consent1 = new Consent { TenantId = tenant1.Id, UserId = user1.Id, ClientId = "client1", ScopesJson = """["openid"]""" };
        var consent2 = new Consent { TenantId = tenant2.Id, UserId = user2.Id, ClientId = "client1", ScopesJson = """["openid"]""" };
        _db.Consents.AddRange(consent1, consent2);
        await _db.SaveChangesAsync();

        // Act: Delete all Tenant 1 consents
        var tenant1Consents = await _db.Consents.Where(c => c.TenantId == tenant1.Id).ToListAsync();
        _db.Consents.RemoveRange(tenant1Consents);
        await _db.SaveChangesAsync();

        // Assert: Tenant 2 data remains intact
        var remainingConsents = await _db.Consents.ToListAsync();
        Assert.HasCount(1, remainingConsents);
        Assert.AreEqual(tenant2.Id, remainingConsents[0].TenantId);
        Assert.AreEqual(user2.Id, remainingConsents[0].UserId);
    }

    #endregion
}

