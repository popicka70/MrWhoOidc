using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.WebAuth.Infrastructure.Startup;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class HttpsCertificateStartupValidatorTests
{
    [TestMethod]
    public void Returns_False_When_Https_Certificate_File_Is_Missing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "https://+:8443;http://+:8080",
                ["Kestrel:Certificates:Default:Path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx")
            })
            .Build();

        var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Returns_True_When_Certificate_File_Is_Readable()
    {
        var certPath = Path.GetTempFileName();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ASPNETCORE_URLS"] = "https://+:8443;http://+:8080",
                    ["Kestrel:Certificates:Default:Path"] = certPath
                })
                .Build();

            var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

            Assert.IsTrue(result);
        }
        finally
        {
            File.Delete(certPath);
        }
    }

    [TestMethod]
    public void Returns_True_When_Https_Is_Not_Configured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "http://+:8080",
                ["Kestrel:Certificates:Default:Path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx")
            })
            .Build();

        var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

        Assert.IsTrue(result);
    }
}