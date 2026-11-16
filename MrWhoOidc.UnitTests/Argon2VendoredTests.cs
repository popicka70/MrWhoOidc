using System.Text;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Guards for the vendored Isopoh sources (<c>externals/isopoh</c>, upstream commit
/// <c>cfcec8465ae181c6dfd209c3661896e53eff19ad</c>): the official reference test vector,
/// reusable-memory semantics of <see cref="Argon2Memory"/>, and compatibility with
/// hashes produced by the previously released NuGet package 2.0.0 (every stored hash
/// predating the switch was produced by it).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Argon2VendoredTests
{
    // PHC winner official test vector — Argon2i, version 19, m=32 KiB, t=3, p=4,
    // tag length 32. From upstream test/Isopoh.Cryptography.Test/argon2-test-vectors.txt.
    private static readonly byte[] KatPassword = Repeat(0x01, 32);
    private static readonly byte[] KatSalt = Repeat(0x02, 16);
    private static readonly byte[] KatSecret = Repeat(0x03, 8);
    private static readonly byte[] KatAssociatedData = Repeat(0x04, 12);
    private static readonly byte[] KatTag = Convert.FromHexString("c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8");

    private static byte[] Repeat(byte b, int count)
    {
        var result = new byte[count];
        Array.Fill(result, b);
        return result;
    }

    [TestMethod]
    public void OfficialReferenceVector_ProducesExpectedTag()
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.DataIndependentAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = 3,
            MemoryCost = 32,
            Lanes = 4,
            Threads = 1,
            Password = KatPassword,
            Salt = KatSalt,
            Secret = KatSecret,
            AssociatedData = KatAssociatedData,
            HashLength = 32,
        };

        using var memory = new Argon2Memory(config, Argon2MemoryPolicy.NoShrink, LockMemoryPolicy.BestEffort);
        memory.Reset(config);
        var encoded = Argon2.Hash(memory);

        var decodeConfig = new Argon2Config();
        Assert.IsTrue(decodeConfig.DecodeString(encoded, out var decoded), "produced hash should decode");
        using (decoded)
        {
            CollectionAssert.AreEqual(KatTag, decoded.Buffer, "computed tag must equal the official reference tag");
        }

        // Classic verification path with the same password/secret/associated data.
        Assert.IsTrue(Argon2.Verify(encoded, config));
        Assert.IsFalse(
            Argon2.Verify(encoded, new Argon2Config { Password = Repeat(0x09, 32), Secret = KatSecret, AssociatedData = KatAssociatedData }),
            "a different password must not verify");
    }

    [TestMethod]
    public void ReusableMemory_SequentialOperations_ReuseSameBuffer()
    {
        // One Argon2Memory instance, reset for two different requests — the exact
        // production pattern from PasswordHasher/Argon2VerificationGate. Both results
        // must compute, encode and verify correctly through the same buffer.
        var password1 = Encoding.UTF8.GetBytes("buffer-reuse-one");
        var password2 = Encoding.UTF8.GetBytes("buffer-reuse-two");

        var config1 = SmallConfig(password1, Convert.FromHexString("101112131415161718191a1b1c1d1e1f"));
        var config2 = SmallConfig(password2, Convert.FromHexString("202122232425262728292a2b2c2d2e2f"));

        using var memory = new Argon2Memory(config1, Argon2MemoryPolicy.NoShrink, LockMemoryPolicy.BestEffort);

        memory.Reset(config1);
        var encoded1 = Argon2.Hash(memory);
        Assert.IsTrue(Argon2.Verify(encoded1, memory), "verification while the first password is loaded must succeed");

        memory.Reset(config2);
        var encoded2 = Argon2.Hash(memory);
        Assert.AreNotEqual(encoded1, encoded2);
        Assert.IsTrue(Argon2.Verify(encoded2, memory));

        // The first result still verifies against its own password (classic path),
        // and the two hashes do not cross-verify.
        Assert.IsTrue(Argon2.Verify(encoded1, new Argon2Config { Password = password1 }));
        Assert.IsFalse(Argon2.Verify(encoded2, new Argon2Config { Password = password1 }));
    }

    private static Argon2Config SmallConfig(byte[] password, byte[] salt) => new()
    {
        Type = Argon2Type.HybridAddressing,
        Version = Argon2Version.Nineteen,
        TimeCost = 2,
        MemoryCost = 8192,
        Lanes = 2,
        Threads = 1,
        Password = password,
        Salt = salt,
        HashLength = 32,
    };

    /// <summary>
    /// A v2 hash produced by the released NuGet Isopoh.Cryptography.Argon2 2.0.0
    /// package with this OP's exact profile (argon2id, m=131072 KiB, t=4, p=4) and a
    /// fixed salt. Every credential row created before the vendoring switch has this
    /// format, so the new path must continue to accept it.
    /// </summary>
    private const string Isopoh2_0_0_V2Hash =
        "v2:$argon2id$v=19$m=131072,t=4,p=4$AAECAwQFBgcICQoLDA0ODw$K4D5FS5ywaMhBEd4aZ45u3Gy6BpwfPgrRoM7gNmL3Uk";

    private const string Isopoh2_0_0_V2HashPassword = "Argon2 backward-compat vector 2026-11-16";

    [TestMethod]
    public void Verify_AcceptsHashProducedByReleasedIsopoh2000()
    {
        using var provider = CreateProvider();
        var hasher = provider.GetRequiredService<IPasswordHasher>();

        Assert.IsTrue(hasher.Verify(Isopoh2_0_0_V2HashPassword, Isopoh2_0_0_V2Hash));
        Assert.IsFalse(hasher.Verify("wrong-password", Isopoh2_0_0_V2Hash));
    }

    [TestMethod]
    public void Verify_MalformedStoredHashes_FailClosed()
    {
        using var provider = CreateProvider();
        var hasher = provider.GetRequiredService<IPasswordHasher>();

        Assert.IsFalse(hasher.Verify("x", "no-prefix-at-all"));
        Assert.IsFalse(hasher.Verify("x", ""));
        Assert.IsFalse(hasher.Verify("x", "v2:"));
        Assert.IsFalse(hasher.Verify("x", "v2:$argon2id$v=19$m=131072,t=4,p=4$!!not-base64!!$####"));
        Assert.IsFalse(hasher.Verify("x", "v9:whatever"));
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:UserAccount:UserAccountDecouplingEnabled"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddMrWhoOidcAuthCore(configuration);
        return services.BuildServiceProvider();
    }
}
