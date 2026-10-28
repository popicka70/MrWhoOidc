using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// M-Plain: the codes of the device authorization grant (RFC 8628) are live credentials and must not sit in the
/// database in plaintext. The device_code has 256 bits of entropy and is looked up by its hash; the short user_code
/// is looked up by its keyed hash when <c>Security:LookupHashKey</c> is configured, and is protected reversibly
/// otherwise.
/// </summary>
[TestClass]
public sealed class DeviceCodeProtectionTests
{
    /// <summary>A protector that really protects, like the DataProtection one of the server.</summary>
    private sealed class DataProtectionSecretProtector(IServiceProvider provider) : ISecretProtector
    {
        private readonly IDataProtector _protector = provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MrWhoOidc.Auth.DeviceCodes.UserCode.v1");

        public string ProtectSigningKeyJwk(string plaintext) => plaintext;
        public string UnprotectSigningKeyJwk(string storedValue) => storedValue;
        public string ProtectTotpSecret(string plaintext) => plaintext;
        public string? UnprotectTotpSecret(string? storedValue) => storedValue;
        public string ProtectProviderSecret(string plaintext) => plaintext;
        public string UnprotectProviderSecret(string storedValue) => storedValue;
        public string ProtectDeviceUserCode(string plaintext) => "dp:v1:" + _protector.Protect(plaintext);
        public string UnprotectDeviceUserCode(string storedValue)
            => storedValue.StartsWith("dp:v1:", StringComparison.Ordinal)
                ? _protector.Unprotect(storedValue["dp:v1:".Length..])
                : storedValue;
        public bool IsProtected(string? storedValue) => storedValue?.StartsWith("dp:v1:", StringComparison.Ordinal) == true;
    }

    private static (DeviceCodeStore store, IDeviceCodeProtector protector, AuthDbContext db, Guid tenantId) CreateStore(
        string? lookupHashKey = "unit-test-lookup-hash-key-0123456789")
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var protector = new DeviceCodeProtector(
            Options.Create(new LookupHashOptions { LookupHashKey = lookupHashKey ?? string.Empty }),
            new DataProtectionSecretProtector(provider),
            NullLogger<DeviceCodeProtector>.Instance);

        var tenantId = Guid.NewGuid();
        return (new DeviceCodeStore(db, protector, NullLogger<DeviceCodeStore>.Instance), protector, db, tenantId);
    }

    [TestMethod]
    public async Task TheDeviceCode_IsFoundByItsHash_AndNotStoredInPlaintext()
    {
        var (store, protector, db, tenantId) = CreateStore();
        await using var _db = db;

        const string deviceCode = "the-device-code";
        var stored = protector.ProtectDeviceCode(deviceCode);
        Assert.IsTrue(stored.StartsWith("sha256:v1:", StringComparison.Ordinal));
        Assert.IsFalse(stored.Contains(deviceCode, StringComparison.Ordinal));

        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId,
            DeviceCodeHash = stored,
            ClientId = "client",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        var found = await store.FindByDeviceCodeAsync(tenantId, deviceCode);
        Assert.IsNotNull(found, "the device code must be found by its hash");

        var missing = await store.FindByDeviceCodeAsync(tenantId, "another-device-code");
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task TheUserCode_IsFoundByItsKeyedHash()
    {
        var (store, protector, db, tenantId) = CreateStore();
        await using var _db = db;

        const string userCode = "ABCD-EFGH";
        var stored = protector.ProtectUserCode(userCode);
        Assert.IsTrue(stored.StartsWith("hmac:v1:", StringComparison.Ordinal), "the user code must be hashed with a key");
        Assert.IsFalse(stored.Contains(userCode, StringComparison.Ordinal));

        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId,
            UserCodeProtected = stored,
            ClientId = "client",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        var found = await store.FindByUserCodeAsync(tenantId, userCode);
        Assert.IsNotNull(found, "the user code must be found by its keyed hash");

        var missing = await store.FindByUserCodeAsync(tenantId, "ZZZZ-ZZZZ");
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task WithoutAKey_TheUserCodeIsProtectedReversibly_AndStillFound()
    {
        var (store, protector, db, tenantId) = CreateStore(lookupHashKey: null);
        await using var _db = db;

        const string userCode = "ABCD-EFGH";
        var stored = protector.ProtectUserCode(userCode);
        Assert.IsTrue(stored.StartsWith("dp:v1:", StringComparison.Ordinal), "the user code must be protected at rest");
        Assert.IsFalse(stored.Contains(userCode, StringComparison.Ordinal));

        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId,
            UserCodeProtected = stored,
            ClientId = "client",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        var found = await store.FindByUserCodeAsync(tenantId, userCode);
        Assert.IsNotNull(found, "the user code must be found even without a keyed hash");
    }

    [TestMethod]
    public async Task ALegacyRowThatStillCarriesThePlaintextCodes_IsFound()
    {
        var (store, _, db, tenantId) = CreateStore();
        await using var _db = db;

        const string deviceCode = "legacy-device-code";
        const string userCode = "LEGACY-CODE";
        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = tenantId,
            DeviceCode = deviceCode,
            UserCode = userCode,
            ClientId = "client",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        Assert.IsNotNull(await store.FindByDeviceCodeAsync(tenantId, deviceCode));
        Assert.IsNotNull(await store.FindByUserCodeAsync(tenantId, userCode));
    }

    [TestMethod]
    public async Task ACodeOfAnotherTenant_IsNotFound()
    {
        var (store, protector, db, tenantId) = CreateStore();
        await using var _db = db;

        const string userCode = "ABCD-EFGH";
        db.DeviceCodes.Add(new DeviceCodeEntry
        {
            TenantId = Guid.NewGuid(),
            UserCodeProtected = protector.ProtectUserCode(userCode),
            ClientId = "client",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        Assert.IsNull(await store.FindByUserCodeAsync(tenantId, userCode));
    }
}
