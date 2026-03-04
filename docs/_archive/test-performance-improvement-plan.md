# Test Performance Improvement Plan

> **ARCHIVED DOCUMENT** - This is a historical planning document from January 2026. Some improvements described may already be implemented.

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

### ✅ Phase 2 - Shared WebApplicationFactory Fixtures (COMPLETED)

**Status:** Implemented and verified  
**Files Created:**
- `Testing/SharedWebAppFixture.cs` - Reusable fixture for sharing WebApplicationFactory across tests in a class

**Files Updated:**
| File | Factory Instances Removed | Notes |
|------|---------------------------|-------|
| `Phase0AugmentedSafetyTests.cs` | 4 | Uses SharedWebAppFixture |
| `CacheHeadersIntegrationTests.cs` | 2 | Uses SharedWebAppFixture, added [DoNotParallelize] |
| `DisplayParameterIntegrationTests.cs` | 1 | Uses SharedWebAppFixture |
| `ProgramSurfaceSnapshotTests.cs` | 3 | Uses ClassInit/ClassCleanup Lazy pattern |
| `DiscoveryMetadataTests.cs` | 5 | 9 tests use Factory, 1 uses CreateFactoryWithConfig (needs isolated), 4 still use CreateFactory (mutate DB) |
| `AdminUiMultiTenantRoutingTests.cs` | 2 | 2 tests share DefaultFactory, 1 needs custom config |

**Total improvement: ~44 seconds (25% faster)**

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

**Estimated Impact:** 30-60 seconds total

---

## Improvement Phases

### Phase 1: Quick Wins (No Test Logic Changes)

#### 1.1 Shared Test Key Provider ✅

Create a static test key provider that generates keys once and reuses them:

```csharp
public static class SharedTestKeys
{
    private static readonly Lazy<RSA> _sharedRsa = new(() => RSA.Create(2048));
    private static readonly Lazy<ECDsa> _sharedEcdsa = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    
    public static RSA GetRsa() => _sharedRsa.Value;
    public static ECDsa GetEcdsa() => _sharedEcdsa.Value;
}
```

**Impact:** 5-10 seconds saved

#### 1.2 Disable Background Services in Tests ✅

Modify `TestWebAppFactory` to disable background services:

```csharp
public static IHostBuilder DisableBackgroundServices(this IHostBuilder builder)
{
    return builder.ConfigureServices(services =>
    {
        // Remove or disable hosted services
        var descriptors = services.Where(s => typeof(IHostedService).IsAssignableFrom(s.ServiceType)).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    });
}
```

**Impact:** 10-20 seconds saved

#### 1.3 Parallel Test Execution

Ensure MSTest parallel execution is enabled and optimize for it:

```csharp
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel, Workers = 0)]
```

**Impact:** 30-50% reduction with proper isolation

### Phase 2: Test Fixture Optimization

#### 2.1 Shared WebApplicationFactory

Create shared fixtures for integration test classes:

```csharp
public class SharedWebAppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly Lazy<SharedWebAppFixture> _instance = new(() => new SharedWebAppFixture());
    
    public static SharedWebAppFixture Instance => _instance.Value;
    
    public Task InitializeAsync() => Task.CompletedTask;
    public new Task DisposeAsync() => Task.CompletedTask; // Don't dispose shared instance
}
```

**Impact:** 20-30 seconds saved

#### 2.2 Shared Database Context

For unit tests, create a shared in-memory database per test class:

```csharp
public class TestClassFixture
{
    protected static readonly AuthDbContext SharedContext = CreateSharedContext();
    
    private static AuthDbContext CreateSharedContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase("SharedTestDb")
            .Options;
        return new AuthDbContext(options);
    }
}
```

**Impact:** 5-10 seconds saved

### Phase 3: Test Architecture Improvements

#### 3.1 Test Categorization

Separate tests by execution time and requirements:

- **Unit Tests:** Fast, isolated, no external dependencies
- **Integration Tests:** Database, HTTP clients, etc.
- **E2E Tests:** Full application stack

Run different categories in parallel pipelines.

#### 3.2 Test Data Management

Implement proper test data setup/teardown:

```csharp
[TestInitialize]
public void Setup()
{
    // Only create necessary test data
    _context.AddRange(_minimalTestData);
    _context.SaveChanges();
}

[TestCleanup]
public void Cleanup()
{
    // Efficient cleanup (truncate vs delete)
    _context.Database.EnsureDeleted();
}
```

---

## Measurement Plan

### Baseline Metrics

```bash
# Run full test suite with timing
dotnet test --logger "console;verbosity=detailed" --results-directory test-results
```

### Per-Test Timing

Enable MSTest performance logging:

```xml
<RunSettings>
  <LoggerRunSettings>
    <Loggers>
      <Logger FriendlyName="Json" Uri="logger://microsoft/TestPlatform/JsonLogger/v1">
        <Configuration>
          <LogFileName>test-results.json</LogFileName>
        </Configuration>
      </Logger>
    </Loggers>
  </LoggerRunSettings>
</RunSettings>
```

### CI Integration

Add test duration tracking to CI:

```yaml
- name: Run Tests
  run: dotnet test --logger "trx;LogFileName=test-results.trx"
  
- name: Publish Test Results
  uses: actions/upload-artifact@v4
  with:
    name: test-results
    path: '**/*.trx'
```

---

## Success Criteria

| Metric | Current | Target | Stretch |
|--------|---------|--------|----------|
| Total Duration | 173s | 60s | 45s |
| Tests/Second | 5.0 | 14.5 | 19.3 |
| RSA.Create Calls | 100+ | 0 | 0 |
| WebApplicationFactory Creations | 15+ | 3 | 1 |

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Shared state between tests | Test flakiness | Careful test isolation, reset state between tests |
| Key reuse in crypto tests | Invalid test scenarios | Some tests need unique keys (replay detection) |
| Parallel execution issues | Intermittent failures | Use [DoNotParallelize] where needed |
| Database contention | Slowdowns | Use separate database names per test class |

---

## Implementation Timeline

| Week | Phase | Tasks |
|------|-------|-------|
| 1 | Phase 1.1 | Create SharedTestKeys, update high-impact test files |
| 2 | Phase 1.2 | Disable background services, verify all tests pass |
| 3 | Phase 2.1 | Implement shared fixtures for top 5 test classes |
| 4 | Phase 2.2 | Extend shared fixtures, add parallel execution |
| 5 | Phase 3 | Test categorization, CI pipeline optimization |
| 6 | Measurement | Final benchmarks, documentation |

---

**Document Owner:** Development Team  
**Review Schedule:** Monthly during implementation  
**Status:** In Progress
