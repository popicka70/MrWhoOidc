using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Services;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class AuthCoreExtensionSmokeTests
{
    [TestMethod]
    public void AuthCore_Registers_Critical_Services()
    {
        var services = new ServiceCollection();
        services.AddMrWhoOidcAuthCore();
        // Instead of resolving (which requires full EF setup), just verify descriptors are present.
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IKeyStore)), "IKeyStore descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IPasswordHasher)), "IPasswordHasher descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ITokenService)), "ITokenService descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ITokenValidator)), "ITokenValidator descriptor missing");
    }
}
