# Test Performance Improvement Plan

> **Current State:** 868 tests taking ~173 seconds (~3 minutes)  
> **Target:** Under 60 seconds  
> **Date:** January 2026  
> **Last Updated:** January 2026

---

## Implementation Progress

### ✅ Phase 1.1 - Shared Test Key Provider (COMPLETED)

**Status:** Implemented and verified  
**Files Created:**
- `TestSupport/SharedTestKeys.cs` - Lazy-loaded shared RSA/ECDSA keys with helper methods

**Files Updated (RSA.Create removed):**
| File | RSA.Create calls removed | Pattern |
|------|-------------------------|---------|
| `DPoPValidatorTests.cs` | 15 | Static properties using SharedTestKeys |
| `JarmServiceTests.cs` | 3 | Static `SharedRsaJwksJson` field |
| `ClientAssertionValidatorTests.cs` | 1 | Uses `SharedTestKeys.CreateClientAssertion()` |
| `RevocationHandlerTests.cs` | 1 | Cached certificate via Lazy<> |
| `UserInfoHandlerTests.cs` | 3 | Static cached security keys |
| `ExternalOidcIntegrationTests.cs` | 1 | Static cached RsaBundle |
| `ProviderKeysPageTests.cs` | 2 | Lazy<string> for PEM exports |
| `DynamicClientRegistrationTests.cs` | 1 | Lazy<string> for JWK JSON |
| `AuthorizationCodeExchangerTests.cs` | 1 | Static cached encryption key |

**Total RSA.Create calls removed from tests:** ~28

**Pattern Applied:**
```csharp
// Before (per-test key generation)
using var rsa = RSA.Create(2048);
var securityKey = new RsaSecurityKey(rsa) { KeyId = "test-key" };

// After (shared key reuse)
var securityKey = SharedTestKeys.GetRsaSecurityKey("test-key");
```

### ✅ Phase 1.3 - Disable Background Services (COMPLETED)

**Status:** Implemented and verified  
**Files Modified:**
- `BackgroundAndBackchannelExtensions.cs` - Added `Testing:DisableBackgroundServices` check
- `TestWebAppFactory.cs` - Sets `Testing:DisableBackgroundServices=true`

**Impact:** All 8 background services now skip registration during tests:
- `ExpiredTokenCleanupService`
- `ParCleanupHostedService`
- `KeyRotationHostedService`
- `ClientSecretExpiryMonitor`
- `LicenseValidationWorker`
- `KeyCacheWarmupService`
- `BackchannelLogoutDispatcher`
- `BackchannelAlertSampler`

### ⏳ Phase 1 - Remaining Work

**RequestObjectValidatorTests.cs** - Has 6+ RSA.Create calls that need more complex refactoring due to unique JTI requirements per test (replay detection tests).

### 📊 Measurements

| Phase | Test Count | Duration | Tests/sec | Notes |
|-------|------------|----------|-----------|-------|
| Baseline | 868 | 173.6s | 5.0 | Before any changes |
| After Phase 1.1 + 1.3 | 870 | 170.5s | 5.1 | All tests passing |

> **Note:** Initial measurements show minimal improvement (~2%). This is expected because:
> 1. RSA key generation is fast (~20-50ms) and the total removed overhead is ~0.5-1.5s
> 2. The dominant cost is WebApplicationFactory startup per integration test
> 3. Phase 2 (shared fixtures) will provide the significant gains

---

## Executive Summary

The MrWhoOidc unit test suite currently takes approximately 3 minutes to complete. Analysis reveals the slowdown is **not** caused by explicit `Task.Delay` or `Thread.Sleep` calls, but rather by:

1. Repeated RSA 2048-bit key generation (~100+ occurrences)
2. Per-test `AuthDbContext` and in-memory database creation
3. `WebApplicationFactory` startup overhead with 8+ background services
4. Lack of shared test fixtures

This document outlines a phased approach to reduce test execution time by 60-70%.

---

## Root Cause Analysis

### 1. Cryptographic Key Generation (High Impact)

**Problem:** Tests generate fresh RSA 2048-bit keys inside each test method.

```csharp
// Found in DPoPValidatorTests, JwtServiceTests, TokenServiceTests, etc.
using var rsa = RSA.Create(2048);  // ~20-50ms per call
```

**Evidence:**
- `DPoPValidatorTests.cs`: 15 tests, each generating 1-2 RSA keys
- `ExternalOidcIntegrationTests.cs`: Multiple RSA key generations
- `ClientAssertionValidatorTests.cs`: Per-test key generation
- 100+ total occurrences of `RSA.Create` or `ECDsa.Create`

**Estimated Impact:** 5-15 seconds total

### 2. In-Memory Database Creation (High Impact)

**Problem:** Each test creates a new `AuthDbContext` with a unique database name.

```csharp
new DbContextOptionsBuilder<AuthDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString())
    .Options
```

**Evidence:**
- 50+ test files with `UseInMemoryDatabase`
- Each creates a fresh EF Core model cache entry
- Model building has non-trivial overhead even when cached

**Estimated Impact:** 10-20 seconds total

### 3. WebApplicationFactory Overhead (Medium Impact)

**Problem:** Integration tests create new `WebApplicationFactory` instances per test.

**Registered Background Services:**
| Service | Startup Behavior |
|---------|------------------|
| `ExpiredTokenCleanupService` | 1 minute initial delay |
| `ParCleanupHostedService` | 10 second startup delay |
| `KeyRotationHostedService` | 10 second startup delay |
| `ClientSecretExpiryMonitor` | 15 second startup delay |
| `LicenseValidationWorker` | Runs immediately |
| `KeyCacheWarmupService` | Fails immediately (no keys) |
| `BackchannelLogoutDispatcher` | Background loop |
| `BackchannelAlertSampler` | Background loop |

**Evidence:**
- 15+ `TestWebAppFactory.CreateInMemory()` calls
- Each spins up full ASP.NET Core host
- Logs show repeated "Failed to warm up signing key cache" warnings

**Estimated Impact:** 20-40 seconds total

### 4. Test Parallelization Constraints (Low Impact)

**Problem:** Some test classes force sequential execution.

```csharp
[TestClass]
[DoNotParallelize]  // Forces sequential execution
public class Phase0AugmentedSafetyTests
```

**Affected Classes:**
- `Phase0AugmentedSafetyTests`
- `DiscoveryMetadataTests`

---

## Improvement Plan

### Phase 1: Quick Wins (Target: -30 seconds)

#### 1.1 Create Shared Test Key Provider

Create a static class that generates keys once and reuses them:

```csharp
// TestSupport/SharedTestKeys.cs
public static class SharedTestKeys
{
    private static readonly Lazy<RSA> _rsa2048 = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> _rsa4096 = new(() => RSA.Create(4096));
    private static readonly Lazy<ECDsa> _ecdsaP256 = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    private static readonly Lazy<ECDsa> _ecdsaP384 = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP384));
    
    public static RSA Rsa2048 => _rsa2048.Value;
    public static RSA Rsa4096 => _rsa4096.Value;
    public static ECDsa EcdsaP256 => _ecdsaP256.Value;
    public static ECDsa EcdsaP384 => _ecdsaP384.Value;
    
    public static RsaSecurityKey GetRsaSecurityKey(string keyId = "test-key")
    {
        var key = new RsaSecurityKey(Rsa2048) { KeyId = keyId };
        return key;
    }
    
    public static SigningCredentials GetRsaSigningCredentials(string keyId = "test-key")
    {
        return new SigningCredentials(GetRsaSecurityKey(keyId), SecurityAlgorithms.RsaSha256);
    }
}
```

**Files to Update:**
- `DPoPValidatorTests.cs` (15 tests)
- `JwtServiceTests.cs`
- `TokenServiceTests.cs`
- `ClientAssertionValidatorTests.cs`
- `ExternalOidcIntegrationTests.cs`
- `RequestObjectValidatorTests.cs`

#### 1.2 Pre-generate Test Keys at Build Time

For tests requiring unique keys, embed pre-generated keys:

```csharp
// TestSupport/EmbeddedTestKeys.cs
public static class EmbeddedTestKeys
{
    // Pre-generated and stored as embedded resources
    public static JsonWebKey[] GetTestJwks() => 
        JsonSerializer.Deserialize<JsonWebKey[]>(
            EmbeddedResource.Read("test-jwks.json"));
}
```

#### 1.3 Disable Background Services in Test Mode

Update `TestWebAppFactory.CreateInMemory()`:

```csharp
b.ConfigureServices(services =>
{
    // Remove background services that aren't needed for tests
    services.RemoveAll<IHostedService>();
    
    // Re-add only essential ones
    services.AddHostedService<DefaultTenantSeedingService>();
});
```

Or add a configuration flag:

```csharp
b.UseSetting("Testing:DisableBackgroundServices", "true");
```

Then in `BackgroundAndBackchannelExtensions.cs`:

```csharp
public static IServiceCollection AddMrWhoOidcBackgroundAndBackchannel(
    this IServiceCollection services, 
    IConfiguration configuration)
{
    if (configuration.GetValue<bool>("Testing:DisableBackgroundServices"))
        return services;
    
    // ... existing registrations
}
```

### Phase 2: Shared Fixtures (Target: -40 seconds)

#### 2.1 Shared WebApplicationFactory per Test Class

Convert integration test classes to use `IClassFixture` pattern (or MSTest equivalent):

```csharp
[TestClass]
public class DiscoveryMetadataTests : IDisposable
{
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static readonly object _lock = new();
    
    private static WebApplicationFactory<Program> Factory
    {
        get
        {
            if (_sharedFactory == null)
            {
                lock (_lock)
                {
                    _sharedFactory ??= (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
                }
            }
            return _sharedFactory;
        }
    }
    
    [ClassCleanup]
    public static void Cleanup() => _sharedFactory?.Dispose();
    
    [TestMethod]
    public async Task Test1()
    {
        using var client = Factory.CreateClient();
        // ...
    }
}
```

#### 2.2 Shared In-Memory Database per Test Class

For tests that don't require isolation:

```csharp
[TestClass]
public class ClientStoreTests
{
    private static AuthDbContext? _sharedDb;
    private static string _dbName = $"ClientStoreTests_{Guid.NewGuid():N}";
    
    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _sharedDb = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options);
    }
    
    [ClassCleanup]
    public static void Cleanup() => _sharedDb?.Dispose();
    
    [TestInitialize]
    public void TestSetup()
    {
        // Clear relevant tables before each test if needed
        _sharedDb!.Clients.RemoveRange(_sharedDb.Clients);
        _sharedDb.SaveChanges();
    }
}
```

### Phase 3: Architectural Improvements (Target: -20 seconds)

#### 3.1 Use ECDsa Instead of RSA Where Possible

ECDSA P-256 key generation is ~10x faster than RSA 2048:

```csharp
// Before: ~30ms
using var rsa = RSA.Create(2048);

// After: ~3ms
using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
```

Update tests that don't specifically require RSA to use ECDSA.

#### 3.2 Create Test-Specific Service Collection Extensions

```csharp
// TestSupport/TestServiceExtensions.cs
public static class TestServiceExtensions
{
    public static IServiceCollection AddTestAuthCore(this IServiceCollection services)
    {
        // Minimal service registration for unit tests
        services.AddSingleton<IPasswordHasher, FastTestPasswordHasher>();
        services.AddSingleton<IKeyStore, InMemoryKeyStore>();
        // ... only essential services
        return services;
    }
}
```

#### 3.3 Implement Test Categories for Selective Execution

```csharp
[TestMethod]
[TestCategory("Fast")]      // < 100ms
[TestCategory("Unit")]
public void QuickUnitTest() { }

[TestMethod]
[TestCategory("Slow")]      // > 1s
[TestCategory("Integration")]
public async Task SlowIntegrationTest() { }
```

Run fast tests during development:
```bash
dotnet test --filter "TestCategory=Fast"
```

### Phase 4: CI/CD Optimizations

#### 4.1 Parallel Test Execution Strategy

Update `MSTestSettings.cs` or add `.runsettings`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <MSTest>
    <Parallelize>
      <Workers>0</Workers> <!-- 0 = number of processors -->
      <Scope>ClassLevel</Scope> <!-- Better isolation than MethodLevel -->
    </Parallelize>
  </MSTest>
</RunSettings>
```

#### 4.2 Test Splitting for CI

Split tests across multiple CI jobs:

```yaml
# .github/workflows/test.yml
jobs:
  test:
    strategy:
      matrix:
        filter: ["TestCategory=Unit", "TestCategory=Integration"]
    steps:
      - run: dotnet test --filter "${{ matrix.filter }}"
```

---

## Implementation Priority

| Priority | Task | Effort | Impact |
|----------|------|--------|--------|
| 🔴 High | 1.1 Shared Test Key Provider | 2 hours | -10s |
| 🔴 High | 1.3 Disable Background Services | 1 hour | -15s |
| 🟡 Medium | 2.1 Shared WebApplicationFactory | 4 hours | -20s |
| 🟡 Medium | 2.2 Shared In-Memory Database | 3 hours | -15s |
| 🟢 Low | 3.1 Switch to ECDsa | 2 hours | -5s |
| 🟢 Low | 3.3 Test Categories | 1 hour | Selective runs |

---

## Validation Metrics

Track these metrics before/after each phase:

```bash
# Measure total test time
dotnet test --verbosity quiet 2>&1 | Select-String "Duration"

# Measure specific test class
dotnet test --filter "FullyQualifiedName~DPoPValidatorTests" --verbosity normal
```

### Baseline Measurements (Current)

| Metric | Value |
|--------|-------|
| Total tests | 868 |
| Total time | ~173s |
| Tests/second | ~5 |

### Target Measurements

| Metric | Target |
|--------|--------|
| Total tests | 868+ |
| Total time | <60s |
| Tests/second | ~15 |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Shared keys cause test pollution | Use unique `KeyId` per test, don't mutate shared keys |
| Shared DB causes flaky tests | Clear tables in `[TestInitialize]`, use transactions |
| Removing background services hides bugs | Keep one integration test class with full services |
| Parallelization causes race conditions | Use `[DoNotParallelize]` only where truly needed |

---

## Appendix: Files to Modify

### High-Priority Files (Key Generation)

1. `DPoPValidatorTests.cs` - 15 `RSA.Create(2048)` calls
2. `ExternalOidcIntegrationTests.cs` - Multiple key generations
3. `JwtServiceTests.cs` - Key generation per test
4. `TokenServiceTests.cs` - Key generation per test
5. `ClientAssertionValidatorTests.cs` - Key generation per test

### Medium-Priority Files (WebApplicationFactory)

1. `Phase0AugmentedSafetyTests.cs` - 4 factory creations
2. `DiscoveryMetadataTests.cs` - 10+ factory creations
3. `CacheHeadersIntegrationTests.cs` - 2 factory creations
4. `ProgramSurfaceSnapshotTests.cs` - 3 factory creations

### Configuration Files

1. `Testing/TestWebAppFactory.cs` - Add service filtering
2. `BackgroundAndBackchannelExtensions.cs` - Add test mode check
3. Add `TestSupport/SharedTestKeys.cs` (new file)
4. Add `.runsettings` file (new file)
