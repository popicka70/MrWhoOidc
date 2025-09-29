using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoOidcClientOptionsValidatorTests
{
    private readonly MrWhoOidcClientOptionsValidator _validator = new();

    [TestMethod]
    public void Validate_Succeeds_ForConfidentialClientWithSecret()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.AreEqual(ValidateOptionsResult.Success, result);
    }

    [TestMethod]
    public void Validate_Fails_WhenSecretMissing()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            PublicClient = false
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "Confidential clients must configure either ClientSecret or ClientAssertion.");
    }

    [TestMethod]
    public void Validate_Fails_ForNonHttpsIssuer_WhenRequired()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "http://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "Issuer must be an absolute HTTPS URI");
    }
}
