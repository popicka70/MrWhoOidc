using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Examples;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class ExampleDataProtectionTests
{
    [TestMethod]
    public void PersistentKeys_RequireCertificate()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeysDirectory"] = "/unused"
        }).Build();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new ServiceCollection().AddExampleDataProtection(configuration));
    }

    [TestMethod]
    public void PersistentKeys_AreEncryptedAndReadableAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mrwho-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=DataProtectionTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificatePath = Path.Combine(directory, "test.pfx");
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-password"));
            var keyDirectory = Path.Combine(directory, "keys");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeysDirectory"] = keyDirectory,
                ["DataProtection:CertificatePath"] = certificatePath,
                ["DataProtection:CertificatePassword"] = "test-password"
            }).Build();

            string protectedValue;
            using (var services = CreateProvider(configuration))
            {
                protectedValue = services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("restart-test").Protect("test-value");
            }

            var keys = Directory.GetFiles(keyDirectory, "*.xml");
            Assert.HasCount(1, keys);
            StringAssert.Contains(File.ReadAllText(keys[0]), "encryptedSecret");

            using var restartedServices = CreateProvider(configuration);
            Assert.AreEqual("test-value", restartedServices.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("restart-test").Unprotect(protectedValue));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExampleDataProtection(configuration);
        return services.BuildServiceProvider();
    }
}
