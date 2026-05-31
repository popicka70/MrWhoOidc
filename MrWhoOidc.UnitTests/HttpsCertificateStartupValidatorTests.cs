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
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
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
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
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
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = "http://+:8080",
                ["Kestrel:Certificates:Default:Path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx")
            })
            .Build();

        var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Returns_False_In_Production_When_Certificate_Password_Is_Default()
    {
        var certPath = Path.GetTempFileName();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_URLS"] = "https://+:8443;http://+:8080",
                    ["Kestrel:Certificates:Default:Path"] = certPath,
                    ["Kestrel:Certificates:Default:Password"] = "changeit"
                })
                .Build();

            var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

            Assert.IsFalse(result);
        }
        finally
        {
            File.Delete(certPath);
        }
    }

    [TestMethod]
    public void Returns_False_In_Production_When_Database_Password_Is_Default()
    {
        var certPath = Path.GetTempFileName();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_URLS"] = "https://+:8443;http://+:8080",
                    ["Kestrel:Certificates:Default:Path"] = certPath,
                    ["Kestrel:Certificates:Default:Password"] = "9vF2rQ7mL4sT8xN3",
                    ["ConnectionStrings:authdb"] = "Host=localhost;Database=authdb;Username=oidc;Password=changeme_password"
                })
                .Build();

            var result = HttpsCertificateStartupValidator.TryValidate(config, NullLogger.Instance);

            Assert.IsFalse(result);
        }
        finally
        {
            File.Delete(certPath);
        }
    }

    [TestMethod]
    public void Returns_True_In_Production_When_Configured_Secrets_Are_Strong()
    {
        var certPath = Path.GetTempFileName();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_URLS"] = "https://+:8443;http://+:8080",
                    ["Kestrel:Certificates:Default:Path"] = certPath,
                    ["Kestrel:Certificates:Default:Password"] = "9vF2rQ7mL4sT8xN3",
                    ["ConnectionStrings:authdb"] = "Host=localhost;Database=authdb;Username=oidc;Password=8kLm4QrT9vX2pNs7"
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
}